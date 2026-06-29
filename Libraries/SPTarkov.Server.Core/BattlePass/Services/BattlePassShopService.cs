using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.BattlePass.ItemControl;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>
///     网页通行证商店：货架 = 玩家在通行证商人已解锁的购买项（按购买权过滤）∪ 管理员自定义条目（对所有人开放）。
///     <para>购买流程严格三步：① 逐项校验仓库内货币是否足额 ② 全部足额才扣减（部分堆叠精确扣减）③ 邮件发货。
///     任一货币不足直接拒绝且不扣任何物品。库存(全局)与限购(每玩家)按 offer 配置生效。</para>
/// </summary>
[Injectable]
public class BattlePassShopService(
    BattlePassService battlePassService,
    BattlePassStashService stashService,
    BattlePassRewardService rewardService,
    ProfileHelper profileHelper,
    SaveServer saveServer,
    ItemSearchService itemSearchService,
    ISptLogger<BattlePassShopService> logger
)
{
    private static readonly object ShopGate = new();

    /// <summary>当前玩家可见的商店货架（含每项是否买得起 / 库存 / 限购剩余）。</summary>
    public ShopCatalog GetCatalog(string profileId)
    {
        lock (ShopGate)
        {
            var catalog = new ShopCatalog();
            var pmc = profileHelper.GetPmcProfile(new MongoId(profileId));
            if (pmc?.Inventory?.Items is null)
            {
                return catalog;
            }

            var season = BattlePassStore.GetSeason();
            var prog = battlePassService.GetOrResetProgress(profileId, season);
            prog.ShopPurchases ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var state = BattlePassStore.GetShopState();
            RollPeriodAndSync(profileId, prog, state);

            foreach (var (offer, custom) in VisibleOffers())
            {
                catalog.Items.Add(ToView(pmc, prog, state, offer, custom));
            }

            catalog.RefreshSeconds = Math.Max(0, state.RefreshSeconds);
            catalog.NextRefreshUtc = state.RefreshSeconds > 0 ? state.PeriodStartUtc + state.RefreshSeconds : 0;
            return catalog;
        }
    }

    /// <summary>执行购买。返回 (是否成功, 展示消息)。</summary>
    public (bool ok, string message) Buy(string profileId, string offerId)
    {
        lock (ShopGate)
        {
            return BuyLocked(profileId, offerId);
        }
    }

    private (bool ok, string message) BuyLocked(string profileId, string offerId)
    {
        if (string.IsNullOrWhiteSpace(offerId))
        {
            return (false, "未指定商品");
        }

        var sessionId = new MongoId(profileId);
        var pmc = profileHelper.GetPmcProfile(sessionId);
        if (pmc?.Inventory?.Items is null)
        {
            return (false, "未找到玩家档案库存");
        }

        var season = BattlePassStore.GetSeason();
        var prog = battlePassService.GetOrResetProgress(profileId, season);
        prog.ShopPurchases ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var periodState = BattlePassStore.GetShopState();
        RollPeriodAndSync(profileId, prog, periodState);

        var match = VisibleOffers().FirstOrDefault(o =>
            string.Equals(o.offer.Id, offerId, StringComparison.OrdinalIgnoreCase));
        if (match.offer is null)
        {
            return (false, "商品不存在或尚未解锁");
        }

        var offer = match.offer;
        var offerKey = OfferKey(match.custom, offer.Id);
        var state = periodState;

        // 限购（每玩家累计购买次数）
        if (offer.BuyLimit > 0 && prog.ShopPurchases.GetValueOrDefault(offerKey) >= offer.BuyLimit)
        {
            return (false, "已达该商品限购上限");
        }

        // 库存配置 <=0 始终表示无限；有限库存从独立的全局销量账本计算剩余量。
        if (RemainingStock(offer, state, offerKey) == 0)
        {
            return (false, "该商品已售罄");
        }

        if (!TryAggregateCosts(offer.Cost, out var costs))
        {
            return (false, "商品价格配置无效，请联系管理员");
        }

        // ① 足额校验：相同 tpl 先合并，任一货币总额不足则整单拒绝，不扣任何物品
        foreach (var cost in costs)
        {
            var have = stashService.CountTpl(pmc, cost.Tpl, requireFir: false);
            if (have < cost.Count)
            {
                var name = ResolveName(cost.Tpl);
                return (false, $"货币不足：需要 {cost.Count} 个「{name}」，仓库仅有 {have}");
            }
        }

        // ② 扣减（校验已通过，逐项足额扣减）。
        //    在线玩家可能在「校验→扣减」窗口内于游戏内花掉同一笔货币（ShopGate 只串行化网页购买，
        //    挡不住游戏内消费），导致多货币订单扣到一半失败。此时把已扣的部分原路退回（邮件），整单回滚，
        //    保证「要么全额扣减发货，要么一分不少地退还」，不会出现部分扣费不发货。
        var refunded = new List<BpReward>();
        foreach (var cost in costs)
        {
            var removed = stashService.RemoveTpl(pmc, sessionId, cost.Tpl, cost.Count, requireFir: false);
            if (removed > 0)
            {
                refunded.Add(new BpReward { Tpl = cost.Tpl, Count = removed, Type = "item" });
            }

            if (removed < cost.Count)
            {
                logger.Error($"[SPT-BattlePass] 商店扣费中断 profile={profileId} offer={offer.Id} tpl={cost.Tpl} 需扣{cost.Count}实扣{removed}，回滚退款");
                saveServer.SaveProfileAsync(sessionId).GetAwaiter().GetResult(); // 落盘已扣状态，避免退款与扣减不一致
                if (refunded.Count > 0)
                {
                    rewardService.Deliver(profileId, refunded, "通行证商店购买失败 · 货币退还");
                }

                return (false, "扣费未完成（货币可能已在游戏内花用），已退还，请重试");
            }
        }

        // 先记录限购与全局销量，避免并发购买超卖；库存配置本身不被改写。
        prog.ShopPurchases[offerKey] = prog.ShopPurchases.GetValueOrDefault(offerKey) + 1;
        state.Sales[offerKey] = state.Sales.GetValueOrDefault(offerKey) + 1;
        BattlePassStore.SaveProgress(profileId, prog);
        BattlePassStore.SaveShopState(state);

        // 扣费后落盘档案
        try
        {
            saveServer.SaveProfileAsync(sessionId).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.Warning($"[SPT-BattlePass] 商店扣费后保存档案失败（内存态已生效）profile={profileId}: {ex.Message}");
        }

        // ③ 发货
        var sellCount = Math.Max(1, offer.SellCount);
        rewardService.Deliver(
            profileId,
            [new BpReward { Tpl = offer.Tpl, Count = sellCount, Type = "item" }],
            $"通行证商店购买：{ResolveName(offer.Tpl, offer.Name)}"
        );

        logger.Info($"[SPT-BattlePass] 商店购买成功 profile={profileId} offer={offer.Id} 发放 {offer.Tpl}×{sellCount}");
        return (true, "购买成功，商品已发送至游戏内邮箱");
    }

    // ============================ 内部 ============================

    /// <summary>
    ///     当前玩家可见的网页货架：<b>仅</b>管理后台「商店」页维护的自定义 offer（对所有玩家开放）。
    ///     已解锁的游戏内商人购买权不再自动并入网页商店——避免后台未配置时仍冒出预设/解锁项。
    /// </summary>
    private static List<(BpTraderOffer offer, bool custom)> VisibleOffers()
    {
        var result = new List<(BpTraderOffer, bool)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var o in BattlePassStore.GetShopOffers())
        {
            if (!string.IsNullOrWhiteSpace(o.Id) && seen.Add(o.Id))
            {
                result.Add((o, true));
            }
        }

        return result;
    }

    private static string OfferKey(bool custom, string offerId)
    {
        return $"{(custom ? "custom" : "trader")}:{offerId}";
    }

    /// <summary>
    ///     按刷新周期滚动：到期则全局库存(销量)清零并 +1 代次；玩家限购代次落后则清空其限购计数。
    ///     在 <see cref="ShopGate"/> 内调用，落盘有变更的 state / progress。
    /// </summary>
    private void RollPeriodAndSync(string profileId, BpProgress prog, BpShopState state)
    {
        if (state.RefreshSeconds > 0)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (state.PeriodStartUtc <= 0)
            {
                state.PeriodStartUtc = now; // 首次设定锚点
                BattlePassStore.SaveShopState(state);
            }
            else if (now >= state.PeriodStartUtc + state.RefreshSeconds)
            {
                // 跨多个周期空窗也只滚一次：库存补满、限购代次前进，锚点对齐到当前周期起点
                var elapsed = now - state.PeriodStartUtc;
                state.PeriodStartUtc += elapsed - elapsed % state.RefreshSeconds;
                state.Epoch++;
                state.Sales.Clear();
                BattlePassStore.SaveShopState(state);
            }
        }

        if (prog.ShopEpoch != state.Epoch)
        {
            prog.ShopEpoch = state.Epoch;
            prog.ShopPurchases.Clear();
            BattlePassStore.SaveProgress(profileId, prog);
        }
    }

    private static int RemainingStock(BpTraderOffer offer, BpShopState state, string offerKey)
    {
        return offer.Stock <= 0 ? -1 : Math.Max(0, offer.Stock - state.Sales.GetValueOrDefault(offerKey));
    }

    private ShopItemView ToView(PmcData pmc, BpProgress prog, BpShopState state, BpTraderOffer offer, bool custom)
    {
        var offerKey = OfferKey(custom, offer.Id);
        var validCosts = TryAggregateCosts(offer.Cost, out var aggregated);
        var costs = validCosts
            ? aggregated.Select(c => new ShopCostView
            {
                Tpl = c.Tpl,
                Name = ResolveName(c.Tpl),
                Count = c.Count,
                Have = stashService.CountTpl(pmc, c.Tpl, requireFir: false),
            })
            .ToList()
            : [];

        var bought = prog.ShopPurchases.GetValueOrDefault(offerKey);
        var remainingStock = RemainingStock(offer, state, offerKey);
        return new ShopItemView
        {
            Id = offer.Id,
            Tpl = offer.Tpl,
            Name = ResolveName(offer.Tpl, offer.Name),
            SellCount = Math.Max(1, offer.SellCount),
            Stock = remainingStock,
            BuyLimit = offer.BuyLimit,
            Bought = bought,
            SoldOut = remainingStock == 0 || (offer.BuyLimit > 0 && bought >= offer.BuyLimit),
            Affordable = validCosts && costs.All(c => c.Have >= c.Count),
            Error = validCosts ? null : "价格配置无效",
            Costs = costs,
        };
    }

    internal static bool TryAggregateCosts(IEnumerable<BpBarterCost>? source, out List<BpBarterCost> costs)
    {
        costs = [];
        var totals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var cost in source ?? [])
        {
            var tpl = cost?.Tpl?.Trim() ?? "";
            if (!MongoId.IsValidMongoId(tpl) || cost!.Count <= 0)
            {
                return false;
            }

            var total = totals.GetValueOrDefault(tpl) + cost.Count;
            if (total > int.MaxValue)
            {
                return false;
            }

            totals[tpl] = total;
        }

        costs = totals.Select(pair => new BpBarterCost { Tpl = pair.Key, Count = (int)pair.Value }).ToList();
        return true;
    }

    private string ResolveName(string tpl, string? preferred = null)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            return preferred!;
        }

        // 网页商店面向中文用户：优先简体中文名（如欧元而非 Euros），不受英文服务端 locale 影响。
        var resolved = MongoId.IsValidMongoId(tpl) ? itemSearchService.ResolveItemNameZh(new MongoId(tpl)) : "";
        return string.IsNullOrWhiteSpace(resolved) ? tpl : resolved;
    }
}

/// <summary>网页商店货架（GET）。</summary>
public class ShopCatalog
{
    public List<ShopItemView> Items { get; set; } = new();

    /// <summary>商品刷新周期（秒，0 = 不刷新）。</summary>
    public int RefreshSeconds { get; set; }

    /// <summary>下次刷新的 Unix 秒时间戳（0 = 不刷新）。供前端展示倒计时。</summary>
    public long NextRefreshUtc { get; set; }
}

public class ShopItemView
{
    public string Id { get; set; } = "";
    public string Tpl { get; set; } = "";
    public string Name { get; set; } = "";
    public int SellCount { get; set; } = 1;
    public int Stock { get; set; } = -1;
    public int BuyLimit { get; set; }
    public int Bought { get; set; }
    public bool SoldOut { get; set; }
    public bool Affordable { get; set; }
    public string? Error { get; set; }
    public List<ShopCostView> Costs { get; set; } = new();
}

public class ShopCostView
{
    public string Tpl { get; set; } = "";
    public string Name { get; set; } = "";
    public int Count { get; set; }
    public int Have { get; set; }
}
