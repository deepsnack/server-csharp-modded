using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.BattlePass.ItemControl;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>
///     网页通行证商店：货架由管理员自定义，对所有玩家开放。
///     <para>购买流程严格三步：① 按购买份数校验仓库内货币、库存与限购 ② 全部通过才扣减（部分堆叠精确扣减）③ 邮件发货。
///     任一条件不满足直接拒绝且不扣任何物品。库存(全局)与限购(每玩家)按 offer 配置生效。</para>
/// </summary>
[Injectable(InjectionType.Singleton)]
public class BattlePassShopService(
    BattlePassService battlePassService,
    BattlePassStashService stashService,
    BattlePassRewardService rewardService,
    BattlePassItemCategoryService categoryService,
    ProfileHelper profileHelper,
    SaveServer saveServer,
    ItemSearchService itemSearchService,
    ISptLogger<BattlePassShopService> logger
)
{
    private static readonly object ShopGate = new();
    internal const int MaxBatchQuantity = 1000;

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

            // 填充分类摘要：只返回当前货架实际包含的分类
            var categoryGroups = catalog.Items
                .GroupBy(i => i.CategoryId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            catalog.Categories = categoryService.GetAllCategories()
                .Where(c => categoryGroups.ContainsKey(c.Id))
                .Select(c => new ShopCategoryView
                {
                    Id = c.Id,
                    Name = c.Name,
                    Order = c.Order,
                    Count = categoryGroups[c.Id],
                })
                .ToList();

            return catalog;
        }
    }

    /// <summary>抽奖类虚拟商品的 rewardType（发放到抽奖钱包而非邮寄实物）。</summary>
    internal static bool IsVirtualRewardType(string? rewardType)
    {
        var t = (rewardType ?? "item").Trim().ToLowerInvariant();
        return t is "lotteryglobaltickets" or "lotterypooltickets" or "lotteryexchangecoins";
    }

    /// <summary>按份数执行购买。返回 (是否成功, 展示消息)。</summary>
    public (bool ok, string message) Buy(string profileId, string offerId, int quantity = 1)
    {
        lock (ShopGate)
        {
            return BuyLocked(profileId, offerId, quantity);
        }
    }

    private (bool ok, string message) BuyLocked(string profileId, string offerId, int quantity)
    {
        if (string.IsNullOrWhiteSpace(offerId))
        {
            return (false, "未指定商品");
        }

        if (quantity <= 0)
        {
            return (false, "购买数量必须大于 0");
        }

        if (quantity > MaxBatchQuantity)
        {
            return (false, $"单次最多购买 {MaxBatchQuantity} 份");
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
        var bought = Math.Max(0, prog.ShopPurchases.GetValueOrDefault(offerKey));
        var sold = Math.Max(0, state.Sales.GetValueOrDefault(offerKey));

        // 限购（每玩家累计购买次数）
        if (offer.BuyLimit > 0)
        {
            var remainingLimit = Math.Max(0, offer.BuyLimit - bought);
            if (remainingLimit == 0)
            {
                return (false, "已达该商品限购上限");
            }

            if (quantity > remainingLimit)
            {
                return (false, $"超过该商品限购，本次最多可购买 {remainingLimit} 份");
            }
        }

        // 库存配置 <=0 始终表示无限；有限库存从独立的全局销量账本计算剩余量。
        var remainingStock = RemainingStock(offer, state, offerKey);
        if (remainingStock == 0)
        {
            return (false, "该商品已售罄");
        }

        if (remainingStock > 0 && quantity > remainingStock)
        {
            return (false, $"库存不足，本次最多可购买 {remainingStock} 份");
        }

        var sellCount = Math.Max(1, offer.SellCount);
        if (quantity > int.MaxValue / sellCount || quantity > int.MaxValue - bought || quantity > int.MaxValue - sold)
        {
            return (false, "购买数量过大");
        }

        if (!TryScaleCosts(offer.Cost, quantity, out var costs))
        {
            return (false, "购买数量过大或商品价格配置无效，请联系管理员");
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
        prog.ShopPurchases[offerKey] = bought + quantity;
        state.Sales[offerKey] = sold + quantity;
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

        // ③ 发货：实物走邮件；虚拟商品（抽奖券/兑换币）经统一奖励接口入抽奖钱包，不邮寄。
        var totalSellCount = sellCount * quantity;
        var rewardType = (offer.RewardType ?? "item").Trim();
        if (IsVirtualRewardType(rewardType))
        {
            var reward = new BpReward { Type = rewardType, Count = totalSellCount, PoolId = offer.PoolId };
            var mailMessage = $"通行证商店购买：{ResolveVirtualName(offer)}";
            battlePassService.GrantRewards(profileId, prog, [reward], mailMessage);
            logger.Info(
                $"[SPT-BattlePass] 商店购买成功 profile={profileId} offer={offer.Id} 购买{quantity}份 发放虚拟商品 {rewardType}×{totalSellCount}"
            );
            return (true, $"购买成功（{quantity} 份），{VirtualDeliveredMessage(rewardType, totalSellCount)}");
        }

        rewardService.Deliver(
            profileId,
            [new BpReward { Tpl = offer.Tpl, Count = totalSellCount, Type = "item" }],
            $"通行证商店购买：{ResolveName(offer.Tpl, offer.Name)}"
        );

        logger.Info(
            $"[SPT-BattlePass] 商店购买成功 profile={profileId} offer={offer.Id} 购买{quantity}份 发放 {offer.Tpl}×{totalSellCount}"
        );
        return (true, $"购买成功（{quantity} 份），商品已发送至游戏内邮箱");
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
    ///     按刷新周期滚动库存(销量)与玩家限购。商品分三种刷新模式：
    ///     <list type="bullet">
    ///       <item>offer.RefreshSeconds == null：继承全局周期（<see cref="BpShopState.RefreshSeconds"/> / <see cref="BpShopState.Epoch"/>）。</item>
    ///       <item>offer.RefreshSeconds == 0：本商品永不刷新（库存/限购终身累计）。</item>
    ///       <item>offer.RefreshSeconds &gt; 0：本商品按 <see cref="BpShopState.OfferPeriods"/> 中独立周期滚动。</item>
    ///     </list>
    ///     在 <see cref="ShopGate"/> 内调用，落盘有变更的 state / progress。
    /// </summary>
    private void RollPeriodAndSync(string profileId, BpProgress prog, BpShopState state)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        prog.ShopOfferEpochs ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // 按刷新模式对当前货架的 offerKey 归类
        var globalKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var independentOffers = new List<(string key, int seconds)>();
        foreach (var (offer, custom) in VisibleOffers())
        {
            var key = OfferKey(custom, offer.Id);
            if (offer.RefreshSeconds is null)
            {
                globalKeys.Add(key);
            }
            else if (offer.RefreshSeconds > 0)
            {
                independentOffers.Add((key, offer.RefreshSeconds.Value));
            }
            // == 0：终身累计，不参与任何周期滚动
        }

        var stateChanged = false;
        var progChanged = false;

        // 1) 全局周期：滚动只清空「继承全局」的商品销量/限购，独立与终身商品不受影响
        if (state.RefreshSeconds > 0)
        {
            if (state.PeriodStartUtc <= 0)
            {
                state.PeriodStartUtc = now; // 首次设定锚点
                stateChanged = true;
            }
            else if (now >= state.PeriodStartUtc + state.RefreshSeconds)
            {
                var elapsed = now - state.PeriodStartUtc;
                state.PeriodStartUtc += elapsed - elapsed % state.RefreshSeconds;
                state.Epoch++;
                foreach (var key in globalKeys)
                {
                    state.Sales.Remove(key);
                }

                stateChanged = true;
            }
        }

        if (prog.ShopEpoch != state.Epoch)
        {
            prog.ShopEpoch = state.Epoch;
            foreach (var key in globalKeys)
            {
                prog.ShopPurchases.Remove(key);
            }

            progChanged = true;
        }

        // 2) 独立周期商品：各自滚动，互不影响
        foreach (var (key, seconds) in independentOffers)
        {
            if (!state.OfferPeriods.TryGetValue(key, out var period))
            {
                period = new BpShopOfferPeriod { PeriodStartUtc = now };
                state.OfferPeriods[key] = period;
                stateChanged = true;
            }
            else if (period.PeriodStartUtc <= 0)
            {
                period.PeriodStartUtc = now;
                stateChanged = true;
            }
            else if (now >= period.PeriodStartUtc + seconds)
            {
                var elapsed = now - period.PeriodStartUtc;
                period.PeriodStartUtc += elapsed - elapsed % seconds;
                period.Epoch++;
                state.Sales.Remove(key);
                stateChanged = true;
            }

            if (prog.ShopOfferEpochs.GetValueOrDefault(key) != period.Epoch)
            {
                prog.ShopOfferEpochs[key] = period.Epoch;
                prog.ShopPurchases.Remove(key);
                progChanged = true;
            }
        }

        if (stateChanged)
        {
            BattlePassStore.SaveShopState(state);
        }

        if (progChanged)
        {
            BattlePassStore.SaveProgress(profileId, prog);
        }
    }

    /// <summary>本商品下次刷新的 Unix 秒（0 = 不刷新）。按三种刷新模式解析。</summary>
    private static long OfferNextRefreshUtc(BpTraderOffer offer, BpShopState state, string offerKey)
    {
        if (offer.RefreshSeconds is null)
        {
            return state.RefreshSeconds > 0 ? state.PeriodStartUtc + state.RefreshSeconds : 0;
        }

        if (offer.RefreshSeconds.Value <= 0)
        {
            return 0; // 终身累计
        }

        var start = state.OfferPeriods.TryGetValue(offerKey, out var period) && period.PeriodStartUtc > 0
            ? period.PeriodStartUtc
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return start + offer.RefreshSeconds.Value;
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
        var sold = state.Sales.GetValueOrDefault(offerKey);
        var maxPurchaseQuantity = validCosts
            ? CalculateMaxPurchaseQuantity(offer.BuyLimit, bought, remainingStock, sold, offer.SellCount, costs)
            : 0;
        var rewardType = (offer.RewardType ?? "item").Trim();
        var virtual_ = IsVirtualRewardType(rewardType);
        var category = categoryService.Resolve(offer.Tpl, isVirtual: virtual_);
        return new ShopItemView
        {
            Id = offer.Id,
            Tpl = offer.Tpl,
            Name = virtual_ ? ResolveVirtualName(offer) : ResolveName(offer.Tpl, offer.Name),
            SellCount = Math.Max(1, offer.SellCount),
            Stock = remainingStock,
            BuyLimit = offer.BuyLimit,
            Bought = bought,
            SoldOut = remainingStock == 0 || (offer.BuyLimit > 0 && bought >= offer.BuyLimit),
            Affordable = validCosts && maxPurchaseQuantity > 0,
            MaxPurchaseQuantity = maxPurchaseQuantity,
            Error = validCosts ? null : "价格配置无效",
            Costs = costs,
            RewardType = virtual_ ? rewardType : "item",
            NextRefreshUtc = OfferNextRefreshUtc(offer, state, offerKey),
            CategoryId = category.Id,
            CategoryName = category.Name,
            CategoryOrder = category.Order,
        };
    }

    /// <summary>虚拟商品展示名：优先管理员填写的 Name，否则按 rewardType 给默认中文名。</summary>
    private static string ResolveVirtualName(BpTraderOffer offer)
    {
        if (!string.IsNullOrWhiteSpace(offer.Name))
        {
            return offer.Name!;
        }

        return (offer.RewardType ?? "").Trim().ToLowerInvariant() switch
        {
            "lotteryglobaltickets" => "通用抽奖券",
            "lotterypooltickets" => "限定抽奖券",
            "lotteryexchangecoins" => "抽奖兑换币",
            _ => "虚拟商品",
        };
    }

    /// <summary>虚拟商品购买成功后的到账提示。</summary>
    private static string VirtualDeliveredMessage(string rewardType, int amount)
    {
        return rewardType.Trim().ToLowerInvariant() switch
        {
            "lotteryglobaltickets" => $"已发放 {amount} 张通用抽奖券至抽奖钱包",
            "lotterypooltickets" => $"已发放 {amount} 张限定抽奖券至抽奖钱包",
            "lotteryexchangecoins" => $"已发放 {amount} 枚抽奖兑换币至抽奖钱包",
            _ => "已到账",
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

    internal static bool TryScaleCosts(IEnumerable<BpBarterCost>? source, int quantity, out List<BpBarterCost> costs)
    {
        costs = [];
        if (quantity <= 0 || quantity > MaxBatchQuantity || !TryAggregateCosts(source, out var unitCosts))
        {
            return false;
        }

        foreach (var cost in unitCosts)
        {
            var total = (long)cost.Count * quantity;
            if (total > int.MaxValue)
            {
                costs = [];
                return false;
            }

            costs.Add(new BpBarterCost { Tpl = cost.Tpl, Count = (int)total });
        }

        return true;
    }

    internal static int CalculateMaxPurchaseQuantity(
        int buyLimit,
        int bought,
        int remainingStock,
        int sold,
        int sellCount,
        IEnumerable<ShopCostView> costs
    )
    {
        bought = Math.Max(0, bought);
        sold = Math.Max(0, sold);
        var max = MaxBatchQuantity;
        max = Math.Min(max, int.MaxValue - bought);
        max = Math.Min(max, int.MaxValue - sold);
        max = Math.Min(max, int.MaxValue / Math.Max(1, sellCount));

        if (buyLimit > 0)
        {
            max = Math.Min(max, Math.Max(0, buyLimit - bought));
        }

        if (remainingStock >= 0)
        {
            max = Math.Min(max, remainingStock);
        }

        foreach (var cost in costs)
        {
            if (cost.Count <= 0)
            {
                return 0;
            }

            max = Math.Min(max, Math.Max(0, cost.Have) / cost.Count);
            max = Math.Min(max, int.MaxValue / cost.Count);
        }

        return Math.Max(0, max);
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

    /// <summary>当前货架实际包含的一级分类列表（去重，按 Handbook 排序）。</summary>
    public List<ShopCategoryView> Categories { get; set; } = new();
}

/// <summary>商店一级分类摘要。</summary>
public class ShopCategoryView
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Order { get; set; }
    public int Count { get; set; }
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
    public int MaxPurchaseQuantity { get; set; }
    public string? Error { get; set; }
    public List<ShopCostView> Costs { get; set; } = new();

    /// <summary>发放方式：item（实物，前端按 tpl 显示物品图标）或抽奖类虚拟商品（不显示物品图标，用徽章标识）。</summary>
    public string RewardType { get; set; } = "item";

    /// <summary>本商品下次刷新的 Unix 秒（0 = 不刷新）。前端按此展示各商品独立倒计时。</summary>
    public long NextRefreshUtc { get; set; }

    /// <summary>一级分类稳定键（Handbook 分类 Id / virtual / other）。</summary>
    public string CategoryId { get; set; } = "";

    /// <summary>一级分类中文显示名。</summary>
    public string CategoryName { get; set; } = "";

    /// <summary>分类排序值（Handbook Order；virtual/other 排最后）。</summary>
    public int CategoryOrder { get; set; }
}

public class ShopCostView
{
    public string Tpl { get; set; } = "";
    public string Name { get; set; } = "";
    public int Count { get; set; }
    public int Have { get; set; }
}
