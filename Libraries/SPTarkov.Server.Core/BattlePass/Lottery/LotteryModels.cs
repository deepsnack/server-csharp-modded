using System.Text.Json.Serialization;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>通行证抽奖模块常量。</summary>
public static class BpLotteryConstants
{
    public const string PoolStatusDraft = "draft";
    public const string PoolStatusScheduled = "scheduled";
    public const string PoolStatusActive = "active";
    public const string PoolStatusPaused = "paused";
    public const string PoolStatusEnded = "ended";
    public const string PoolStatusArchived = "archived";

    public const string PoolTypeRepeatable = "repeatable";
    public const string PoolTypeNonRepeatable = "nonRepeatable";

    public const string ProbabilityEqual = "equal";
    public const string ProbabilityWeight = "weight";

    public const string CostStashItems = "stashItems";
    public const string CostLotteryTickets = "lotteryTickets";

    public const string DrawOnce = "once";
    public const string DrawTen = "ten";

    public const string TransactionPending = "pending";
    public const string TransactionCommitted = "committed";
    public const string TransactionFailed = "failed";
    public const string TransactionNeedsManualReview = "needsManualReview";
}
/// <summary>抽奖模块全局设置。</summary>
public record BpLotterySettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("playerEntryEnabled")]
    public bool PlayerEntryEnabled { get; set; } = true;

    [JsonPropertyName("exchangeShopEnabled")]
    public bool ExchangeShopEnabled { get; set; } = true;

    [JsonPropertyName("broadcastEnabled")]
    public bool BroadcastEnabled { get; set; } = true;

    [JsonPropertyName("activationRedeemEnabled")]
    public bool ActivationRedeemEnabled { get; set; } = true;

    [JsonPropertyName("playerRecordDisplayLimit")]
    public int PlayerRecordDisplayLimit { get; set; } = 50;

    /// <summary>full | masked。</summary>
    [JsonPropertyName("broadcastNameMode")]
    public string BroadcastNameMode { get; set; } = "full";

    /// <summary>服务器用于每日/每周限购刷新的时区。空值表示服务器本地时区。</summary>
    [JsonPropertyName("timeZoneId")]
    public string? TimeZoneId { get; set; }
}

/// <summary>抽奖池配置。</summary>
public record BpLotteryPool
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("iconUrl")]
    public string? IconUrl { get; set; }

    [JsonPropertyName("coverUrl")]
    public string? CoverUrl { get; set; }

    [JsonPropertyName("sortOrder")]
    public int SortOrder { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("visibleToPlayers")]
    public bool VisibleToPlayers { get; set; } = true;

    [JsonPropertyName("status")]
    public string Status { get; set; } = BpLotteryConstants.PoolStatusDraft;

    [JsonPropertyName("pauseVisibleToPlayers")]
    public bool PauseVisibleToPlayers { get; set; } = true;

    [JsonPropertyName("followSeason")]
    public bool FollowSeason { get; set; } = true;

    /// <summary>不跟随赛季时的开始 Unix 秒；跟随赛季时由当前赛季覆盖。</summary>
    [JsonPropertyName("startUtc")]
    public long StartUtc { get; set; }

    /// <summary>不跟随赛季时的结束 Unix 秒；跟随赛季时由当前赛季覆盖。</summary>
    [JsonPropertyName("endUtc")]
    public long EndUtc { get; set; }

    /// <summary>repeatable | nonRepeatable。</summary>
    [JsonPropertyName("poolType")]
    public string PoolType { get; set; } = BpLotteryConstants.PoolTypeRepeatable;

    /// <summary>equal | weight。</summary>
    [JsonPropertyName("probabilityMode")]
    public string ProbabilityMode { get; set; } = BpLotteryConstants.ProbabilityEqual;

    /// <summary>stashItems | lotteryTickets。</summary>
    [JsonPropertyName("costType")]
    public string CostType { get; set; } = BpLotteryConstants.CostLotteryTickets;

    [JsonPropertyName("singleCost")]
    public BpLotteryCost SingleCost { get; set; } = new();

    /// <summary>仅抽奖券十连折扣价；仓库物品十连固定单抽 x10。</summary>
    [JsonPropertyName("tenDrawCostOverride")]
    public BpLotteryCost? TenDrawCostOverride { get; set; }

    /// <summary>不可重复池每一抽的阶梯价格。</summary>
    [JsonPropertyName("stepCosts")]
    public List<BpLotteryStepCost> StepCosts { get; set; } = new();

    [JsonPropertyName("pityEnabled")]
    public bool PityEnabled { get; set; }

    [JsonPropertyName("pityCount")]
    public int PityCount { get; set; }

    [JsonPropertyName("prizes")]
    public List<BpLotteryPrize> Prizes { get; set; } = new();

    [JsonPropertyName("createdUtc")]
    public long CreatedUtc { get; set; }

    [JsonPropertyName("updatedUtc")]
    public long UpdatedUtc { get; set; }
}

/// <summary>抽奖奖项。奖励本体复用通行证奖励轨四类 <see cref="BpReward"/>。</summary>
public record BpLotteryPrize
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("reward")]
    public BpReward Reward { get; set; } = new();

    [JsonPropertyName("weight")]
    public int Weight { get; set; } = 1;

    /// <summary>稀有度，仅驱动前端开箱光柱配色/特效等级，不参与玩法逻辑。common | rare | epic | legendary。</summary>
    [JsonPropertyName("rarity")]
    public string Rarity { get; set; } = "common";

    [JsonPropertyName("isGrandPrize")]
    public bool IsGrandPrize { get; set; }

    [JsonPropertyName("broadcastWhenWon")]
    public bool BroadcastWhenWon { get; set; }

    [JsonPropertyName("convertDuplicateToExchangeCoin")]
    public bool ConvertDuplicateToExchangeCoin { get; set; } = true;

    [JsonPropertyName("duplicateExchangeCoinAmount")]
    public int DuplicateExchangeCoinAmount { get; set; }
}

/// <summary>抽奖代价。</summary>
public record BpLotteryCost
{
    /// <summary>stashItems | lotteryTickets。</summary>
    [JsonPropertyName("costType")]
    public string CostType { get; set; } = BpLotteryConstants.CostLotteryTickets;

    [JsonPropertyName("ticketAmount")]
    public int TicketAmount { get; set; } = 1;

    /// <summary>仓库物品代价复用网页商店价格结构。</summary>
    [JsonPropertyName("stashItems")]
    public List<BpBarterCost> StashItems { get; set; } = new();
}

public record BpLotteryStepCost
{
    /// <summary>从 1 开始的第 N 抽。</summary>
    [JsonPropertyName("drawNumber")]
    public int DrawNumber { get; set; }

    [JsonPropertyName("cost")]
    public BpLotteryCost Cost { get; set; } = new();
}

/// <summary>玩家抽奖钱包。</summary>
public record BpLotteryWallet
{
    [JsonPropertyName("profileId")]
    public string ProfileId { get; set; } = "";

    [JsonPropertyName("globalTickets")]
    public int GlobalTickets { get; set; }

    [JsonPropertyName("poolTickets")]
    public Dictionary<string, int> PoolTickets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("exchangeCoins")]
    public int ExchangeCoins { get; set; }

    [JsonPropertyName("updatedUtc")]
    public long UpdatedUtc { get; set; }
}

/// <summary>玩家在某个奖池内的进度。</summary>
public record BpLotteryPoolProgress
{
    [JsonPropertyName("profileId")]
    public string ProfileId { get; set; } = "";

    [JsonPropertyName("poolId")]
    public string PoolId { get; set; } = "";

    [JsonPropertyName("successfulDrawCount")]
    public int SuccessfulDrawCount { get; set; }

    [JsonPropertyName("pityCounter")]
    public int PityCounter { get; set; }

    [JsonPropertyName("drawnPrizeIds")]
    public HashSet<string> DrawnPrizeIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("updatedUtc")]
    public long UpdatedUtc { get; set; }
}

/// <summary>单次奖项结果记录；十连会写 10 条，使用同一个 RequestId。</summary>
public record BpLotteryDrawRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("profileId")]
    public string ProfileId { get; set; } = "";

    [JsonPropertyName("nicknameSnapshot")]
    public string? NicknameSnapshot { get; set; }

    [JsonPropertyName("poolId")]
    public string PoolId { get; set; } = "";

    [JsonPropertyName("poolNameSnapshot")]
    public string PoolNameSnapshot { get; set; } = "";

    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = "";

    [JsonPropertyName("drawMode")]
    public string DrawMode { get; set; } = BpLotteryConstants.DrawOnce;

    [JsonPropertyName("drawIndexInRequest")]
    public int DrawIndexInRequest { get; set; } = 1;

    [JsonPropertyName("prizeId")]
    public string PrizeId { get; set; } = "";

    [JsonPropertyName("prizeNameSnapshot")]
    public string PrizeNameSnapshot { get; set; } = "";

    [JsonPropertyName("prizeType")]
    public string PrizeType { get; set; } = "item";

    [JsonPropertyName("prizeIconSnapshot")]
    public string? PrizeIconSnapshot { get; set; }

    [JsonPropertyName("isGrandPrize")]
    public bool IsGrandPrize { get; set; }

    /// <summary>抽中时奖项稀有度快照，供前端开箱揭晓取色。</summary>
    [JsonPropertyName("raritySnapshot")]
    public string RaritySnapshot { get; set; } = "common";

    /// <summary>该奖项是否触发广播快照，供前端自抽广播高亮判定。</summary>
    [JsonPropertyName("broadcastWhenWon")]
    public bool BroadcastWhenWon { get; set; }

    [JsonPropertyName("probabilitySnapshot")]
    public decimal ProbabilitySnapshot { get; set; }

    [JsonPropertyName("weightSnapshot")]
    public int WeightSnapshot { get; set; }

    [JsonPropertyName("convertedToExchangeCoin")]
    public bool ConvertedToExchangeCoin { get; set; }

    [JsonPropertyName("exchangeCoinAmount")]
    public int ExchangeCoinAmount { get; set; }

    [JsonPropertyName("costSnapshot")]
    public BpLotteryCostSnapshot CostSnapshot { get; set; } = new();

    [JsonPropertyName("createdUtc")]
    public long CreatedUtc { get; set; }
}

public record BpLotteryCostSnapshot
{
    [JsonPropertyName("costType")]
    public string CostType { get; set; } = BpLotteryConstants.CostLotteryTickets;

    [JsonPropertyName("globalTickets")]
    public int GlobalTickets { get; set; }

    [JsonPropertyName("poolTickets")]
    public int PoolTickets { get; set; }

    [JsonPropertyName("stashItems")]
    public List<BpBarterCost> StashItems { get; set; } = new();
}

/// <summary>抽奖事务记录，用于幂等与异常恢复。</summary>
public record BpLotteryTransaction
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("profileId")]
    public string ProfileId { get; set; } = "";

    [JsonPropertyName("poolId")]
    public string PoolId { get; set; } = "";

    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = "";

    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = BpLotteryConstants.TransactionPending;

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("requestSnapshot")]
    public string? RequestSnapshot { get; set; }

    [JsonPropertyName("resultSnapshot")]
    public string? ResultSnapshot { get; set; }

    [JsonPropertyName("createdUtc")]
    public long CreatedUtc { get; set; }

    [JsonPropertyName("updatedUtc")]
    public long UpdatedUtc { get; set; }

    [JsonPropertyName("expiresUtc")]
    public long ExpiresUtc { get; set; }
}

/// <summary>抽奖兑换商店商品。</summary>
public record BpLotteryShopItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("iconUrl")]
    public string? IconUrl { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = BpLotteryConstants.PoolStatusDraft;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("visibleToPlayers")]
    public bool VisibleToPlayers { get; set; } = true;

    [JsonPropertyName("sortOrder")]
    public int SortOrder { get; set; }

    [JsonPropertyName("startUtc")]
    public long StartUtc { get; set; }

    [JsonPropertyName("endUtc")]
    public long EndUtc { get; set; }

    [JsonPropertyName("price")]
    public int Price { get; set; } = 1;

    /// <summary>none | lifetime | daily | weekly | season。</summary>
    [JsonPropertyName("limitType")]
    public string LimitType { get; set; } = "none";

    [JsonPropertyName("limitCount")]
    public int LimitCount { get; set; }

    [JsonPropertyName("reward")]
    public BpReward Reward { get; set; } = new();
}

public record BpLotteryShopPurchaseProgress
{
    [JsonPropertyName("profileId")]
    public string ProfileId { get; set; } = "";

    [JsonPropertyName("items")]
    public Dictionary<string, BpLotteryShopPurchaseCounter> Items { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public record BpLotteryShopPurchaseCounter
{
    [JsonPropertyName("lifetime")]
    public int Lifetime { get; set; }

    [JsonPropertyName("dailyKey")]
    public string? DailyKey { get; set; }

    [JsonPropertyName("daily")]
    public int Daily { get; set; }

    [JsonPropertyName("weeklyKey")]
    public string? WeeklyKey { get; set; }

    [JsonPropertyName("weekly")]
    public int Weekly { get; set; }

    [JsonPropertyName("seasonId")]
    public string? SeasonId { get; set; }

    [JsonPropertyName("season")]
    public int Season { get; set; }
}

/// <summary>抽奖模块后台操作日志。</summary>
public record BpLotteryAuditLog
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("actor")]
    public string? Actor { get; set; }

    [JsonPropertyName("targetId")]
    public string? TargetId { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("profileId")]
    public string? ProfileId { get; set; }

    [JsonPropertyName("poolId")]
    public string? PoolId { get; set; }

    [JsonPropertyName("batchTag")]
    public string? BatchTag { get; set; }

    [JsonPropertyName("createdUtc")]
    public long CreatedUtc { get; set; }
}
