using System.Security.Cryptography;
using System.Text;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Services;
using Path = System.IO.Path; // 消歧义：Tables 命名空间也有 Path 类型

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>
///     通行证商人：把一个完全由后台配置驱动的自定义商人注入 SPT 数据库（纯服务端，零 Harmony）。
///     - 商人元信息（名称/昵称/介绍/货币/头像等）来自 <c>trader-meta.json</c>。
///     - 货架商品（tpl/价格/库存/限购）来自 <c>trader.json</c>，每项以物易物计价。
///     - 「购买权限」逐玩家解锁：由 <see cref="BattlePassTraderAccessService"/> 按通行证自有账本过滤，
///       不再依赖会被 SPT 存档修复器清除的虚拟任务。
///     - locale 经 <see cref="SPTarkov.Server.Core.Utils.Json.LazyLoad{T}.AddTransformer"/> 安全注入。
///     - 头像经 <see cref="ImageRouter.AddRoute"/> serve。
///     保存配置后可重复调用 <see cref="Sync"/> 热重注入（客户端商人界面可能需重进/重登刷新）。
/// </summary>
[Injectable(InjectionType.Singleton)]
public class BattlePassTraderSync(
    DatabaseService databaseService,
    ImageRouter imageRouter,
    TraderAssortHelper traderAssortHelper,
    TraderHelper traderHelper,
    BattlePassItemBuilder itemBuilder,
    ISptLogger<BattlePassTraderSync> logger
)
{
    /// <summary>通行证商人固定 id。</summary>
    public const string TraderIdHex = "66f1b2c3d4e5a6b7c8d90011";

    // 没有自定义头像时回退到内置 Therapist 头像，保证商人头像始终有效。
    private const string FallbackAvatar = "/files/trader/avatar/59b91cab86f77469aa5343ca.jpg";

    private bool _localeHooked;

    public static MongoId TraderId => new(TraderIdHex);

    /// <summary>旧版购买权虚拟任务 id；仅用于重置时清理历史存档残留。</summary>
    public static MongoId UnlockQuestId(string offerId) => DeterministicId(offerId, "bp-offer-unlock");

    /// <summary>某 offer 在货架里的根 item id（确定性，questassort/barter 据此挂钩）。</summary>
    public static MongoId OfferRootItemId(string offerId) => DeterministicId(offerId, "bp-offer-root");

    /// <summary>构建并注入（或重注入）通行证商人到 DB。OnLoad 及后台保存后调用。</summary>
    public void Sync()
    {
        try
        {
            var cfg = BattlePassStore.GetTraderConfig();
            var offers = BattlePassStore.GetOffers();

            var assort = BuildAssort(offers);
            var trader = new Trader
            {
                Base = BuildBase(cfg),
                Assort = assort,
                Dialogue = new Dictionary<string, List<string>?>(),
                QuestAssort = new Dictionary<string, Dictionary<MongoId, MongoId>>
                {
                    ["success"] = new(),
                },
            };

            traderHelper.SetTraderUpdateSeconds(TraderId, cfg.ResupplySeconds, cfg.Name);
            databaseService.GetTables().Traders[TraderId] = trader;
            traderAssortHelper.InvalidateQuestAssortCache();

            RegisterAvatar(cfg);
            HookLocale(cfg);

            logger.Success(
                $"[SPT-BattlePass] 通行证商人已注入 (id={TraderIdHex}, 配置 {offers.Count} 项，实际注入 {assort.Items.Count} 项)。"
            );
        }
        catch (Exception ex)
        {
            logger.Error($"[SPT-BattlePass] 通行证商人注入失败: {ex.Message}");
        }
    }

    private TraderBase BuildBase(BpTraderConfig cfg)
    {
        var currency = Enum.TryParse<CurrencyType>(cfg.Currency, true, out var c) ? c : CurrencyType.RUB;
        var resupply = (int)DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, cfg.ResupplySeconds)).ToUnixTimeSeconds();
        var hasAvatar = TryResolveAvatarFile(cfg, out var avatarPath);
        var avatarUrl = hasAvatar
            ? $"/files/trader/avatar/{TraderIdHex}{Path.GetExtension(avatarPath).ToLowerInvariant()}"
            : FallbackAvatar;

        return new TraderBase
        {
            Id = TraderId,
            Name = cfg.Name,
            Nickname = cfg.Nickname,
            Surname = cfg.Surname,
            Location = cfg.Location,
            Avatar = avatarUrl,
            Currency = currency,
            AvailableInRaid = false,
            IsAvailableInPVE = true,
            UnlockedByDefault = cfg.UnlockedByDefault,
            BalanceRub = 1_000_000_000,
            BalanceDollar = 0,
            BalanceEuro = 0,
            BuyerUp = false,
            CustomizationSeller = false,
            Discount = 0,
            DiscountEnd = 0,
            GridHeight = 200,
            NextResupply = resupply,
            ProhibitedItemsSellModifier = 0,
            MainDialogue = null,
            Medic = false,
            IsCanTransferItems = false,
            IsCanTransferItemsFromPve = false,
            SellCategory = new List<string>(),
            ItemsBuy = new ItemBuyData { Category = new HashSet<MongoId>(), IdList = new HashSet<MongoId>() },
            ItemsBuyProhibited = new ItemBuyData { Category = new HashSet<MongoId>(), IdList = new HashSet<MongoId>() },
            TransferableItems = new ItemBuyData { Category = new HashSet<MongoId>(), IdList = new HashSet<MongoId>() },
            ProhibitedTransferableItems = new ItemBuyData { Category = new HashSet<MongoId>(), IdList = new HashSet<MongoId>() },
            Insurance = new TraderInsurance
            {
                Availability = cfg.InsuranceAvailable,
                ExcludedCategory = new List<MongoId>(),
                MaxReturnHour = 24,
                MaxStorageTime = 480,
                MinPayment = 0,
                MinReturnHour = 1,
            },
            Repair = new TraderRepair
            {
                Availability = cfg.RepairAvailable,
                Currency = "5449016a4bdc2d6f028b456f",
                CurrencyCoefficient = 1,
                ExcludedCategory = new List<MongoId>(),
                ExcludedIdList = new List<string>(),
                Quality = 0,
                PriceRate = 0,
            },
            LoyaltyLevels = new List<TraderLoyaltyLevel>
            {
                new()
                {
                    BuyPriceCoefficient = 100,
                    ExchangePriceCoefficient = 0,
                    HealPriceCoefficient = 100,
                    InsurancePriceCoefficient = 100,
                    MinLevel = 0,
                    MinSalesSum = 0,
                    MinStanding = 0,
                    RepairPriceCoefficient = 100,
                },
            },
        };
    }

    internal TraderAssort BuildAssort(List<BpTraderOffer> offers)
    {
        var items = new List<Item>();
        var barter = new Dictionary<MongoId, List<List<BarterScheme>>>();
        var loyal = new Dictionary<MongoId, int>();

        foreach (var offer in offers)
        {
            if (string.IsNullOrWhiteSpace(offer.Id) || string.IsNullOrWhiteSpace(offer.Tpl))
            {
                continue;
            }

            MongoId tpl;
            try
            {
                tpl = new MongoId(offer.Tpl.Trim());
            }
            catch
            {
                logger.Warning($"[SPT-BattlePass] 货架项 {offer.Id} 的 tpl 非法，已跳过: {offer.Tpl}");
                continue;
            }

            // mod 物品兜底：商品 tpl 不在物品库（mod 被删除）→ 跳过该货架项的注入，配置保留，mod 装回自动恢复
            if (!databaseService.GetItems().ContainsKey(tpl))
            {
                logger.Warning($"[SPT-BattlePass] 货架项 {offer.Id} 的商品 {offer.Tpl} 不在物品库（mod 已删除？），本次未注入");
                continue;
            }

            var rootId = OfferRootItemId(offer.Id);
            var unlimited = offer.Stock <= 0;

            // 枪/甲/盔按默认完整形态上架（枪=默认改装预设，甲盔=带插板内衬），其余物品为单件。
            var built = itemBuilder.Build(tpl, unlimited ? 999_999 : offer.Stock, rootId);
            var root = built[0];
            root.ParentId = "hideout";
            root.SlotId = "hideout";
            root.Upd = new Upd
            {
                StackObjectsCount = unlimited ? 999_999 : offer.Stock,
                UnlimitedCount = unlimited,
                BuyRestrictionMax = offer.BuyLimit > 0 ? offer.BuyLimit : null,
                BuyRestrictionCurrent = offer.BuyLimit > 0 ? 0 : null,
            };
            items.AddRange(built);

            // 以物易物价格：同一组内多项需同时支付（cost 为空 = 0 价免费取）
            var scheme = new List<BarterScheme>();
            foreach (var cost in offer.Cost ?? new List<BpBarterCost>())
            {
                if (string.IsNullOrWhiteSpace(cost.Tpl) || cost.Count <= 0)
                {
                    continue;
                }

                try
                {
                    scheme.Add(new BarterScheme { Template = new MongoId(cost.Tpl.Trim()), Count = cost.Count });
                }
                catch
                {
                    logger.Warning($"[SPT-BattlePass] 货架项 {offer.Id} 的支付 tpl 非法，已忽略该项: {cost.Tpl}");
                }
            }

            barter[rootId] = new List<List<BarterScheme>> { scheme };
            // 仍需出现在 loyal_level_items，供原生忠诚度与购买流程识别。
            loyal[rootId] = 1;
        }

        return new TraderAssort
        {
            NextResupply = 0,
            Items = items,
            BarterScheme = barter,
            LoyalLevelItems = loyal,
        };
    }

    private void RegisterAvatar(BpTraderConfig cfg)
    {
        if (!TryResolveAvatarFile(cfg, out var path))
        {
            return; // 用回退头像，无需注册路由
        }

        // ImageRouter 按去扩展名的小写 key 匹配；TraderBase.Avatar = /files/trader/avatar/{id}.png
        imageRouter.AddRoute($"/files/trader/avatar/{TraderIdHex}", path);
    }

    private static bool TryResolveAvatarFile(BpTraderConfig cfg, out string path)
    {
        path = BattlePassStore.TraderAvatarPath(cfg.AvatarFile);
        return File.Exists(path);
    }

    /// <summary>把商人名/昵称/介绍等注入每个语言的 locale（幂等：只挂一次 transformer，从 store 实时取值）。</summary>
    private void HookLocale(BpTraderConfig _)
    {
        if (_localeHooked)
        {
            return; // transformer 内部每次实时读取 store，配置改动无需重挂
        }

        var global = databaseService.GetTables().Locales.Global;
        foreach (var (_, lazy) in global)
        {
            lazy.AddTransformer(dict =>
            {
                if (dict is null)
                {
                    return dict;
                }

                var cfg = BattlePassStore.GetTraderConfig();
                dict[$"{TraderIdHex} FullName"] = cfg.Name;
                dict[$"{TraderIdHex} FirstName"] = cfg.Name;
                dict[$"{TraderIdHex} Nickname"] = cfg.Nickname;
                dict[$"{TraderIdHex} Location"] = cfg.Location;
                dict[$"{TraderIdHex} Description"] = cfg.Description;
                return dict;
            });
        }

        _localeHooked = true;
    }

    private static MongoId DeterministicId(string seed, string salt)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(salt + ":" + seed));
        return new MongoId(Convert.ToHexString(bytes).ToLowerInvariant()[..24]);
    }
}
