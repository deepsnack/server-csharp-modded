using System.Text.Json.Serialization;

namespace SPTarkov.Server.Core.BattlePass;

// ============================================================================
//  战斗通行证（Battle Pass / 通行证）数据模型
//  全部用 mod 本地 record，不直接序列化 core 的 Quest/Reward，避免反序列化耦合。
//  落盘目录：SPT_Data/battlepass/*.json（详见 BattlePassStore）。
// ============================================================================

/// <summary>单个奖励项：物品模板 + 数量。发奖时转换为 core 的 Item 列表经邮件发放。</summary>
public record BpReward
{
    /// <summary>物品模板 id（tpl，例如卢布 5449016a4bdc2d6f028b456f）。</summary>
    [JsonPropertyName("tpl")]
    public string Tpl { get; set; } = "";

    /// <summary>数量（可堆叠物品为堆叠数）。</summary>
    [JsonPropertyName("count")]
    public int Count { get; set; } = 1;

    /// <summary>展示名（仅 UI 用，可空）。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>是否核心大奖（UI 放大高亮）。</summary>
    [JsonPropertyName("featured")]
    public bool Featured { get; set; }

    /// <summary>
    ///     奖励兑现方式：
    ///     <list type="bullet">
    ///       <item><c>item</c>（默认）：直接邮寄实物（沿用原有行为，<see cref="Tpl"/>+<see cref="Count"/>）。</item>
    ///       <item><c>purchaseRight</c>：解锁「可在通行证商人处购买某商品」，领取后把 <see cref="OfferId"/> 对应货架项放出给该玩家。</item>
    ///       <item><c>recipe</c>：直接解锁藏身处制造配方（不经商人），<see cref="RecipeId"/> = production id。</item>
    ///       <item><c>title</c>：领取后解锁一个称号（见 <see cref="TitleId"/>），不动游戏档案。</item>
    ///     </list>
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "item";

    /// <summary>type=purchaseRight 时关联的商人货架 offer id（见 <see cref="BpTraderOffer.Id"/>）。</summary>
    [JsonPropertyName("offerId")]
    public string? OfferId { get; set; }

    /// <summary>type=recipe 时要解锁的藏身处 production id。</summary>
    [JsonPropertyName("recipeId")]
    public string? RecipeId { get; set; }

    /// <summary>type=title 时要授予的称号 id（见 <see cref="BpTitle.Id"/>）。</summary>
    [JsonPropertyName("titleId")]
    public string? TitleId { get; set; }
}

/// <summary>某一等级的双轨奖励。</summary>
public record BpLevelRewards
{
    [JsonPropertyName("free")]
    public List<BpReward> Free { get; set; } = new();

    [JsonPropertyName("premium")]
    public List<BpReward> Premium { get; set; } = new();
}

/// <summary>赛季配置。</summary>
public record BpSeason
{
    [JsonPropertyName("seasonId")]
    public string SeasonId { get; set; } = "S1";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "Season 1";

    /// <summary>赛季开始 Unix 秒。</summary>
    [JsonPropertyName("startUtc")]
    public long StartUtc { get; set; }

    /// <summary>赛季结束 Unix 秒。</summary>
    [JsonPropertyName("endUtc")]
    public long EndUtc { get; set; }

    [JsonPropertyName("maxLevel")]
    public int MaxLevel { get; set; } = 50;

    /// <summary>
    ///     每级所需经验曲线（索引 0 = 1→2 级所需）。为空时由 BaseXp + Growth 线性推导。
    /// </summary>
    [JsonPropertyName("xpCurve")]
    public List<int> XpCurve { get; set; } = new();

    /// <summary>曲线为空时的基础每级经验。</summary>
    [JsonPropertyName("baseXp")]
    public int BaseXp { get; set; } = 1000;

    /// <summary>曲线为空时每级经验线性增量。</summary>
    [JsonPropertyName("xpGrowthPerLevel")]
    public int XpGrowthPerLevel { get; set; } = 100;

    /// <summary>付费轨经验倍率（解锁付费轨后任务经验乘数）。</summary>
    [JsonPropertyName("premiumXpMultiplier")]
    public double PremiumXpMultiplier { get; set; } = 1.0;

    /// <summary>
    ///     满级后每完成一轮「循环奖励」所需经验（满级后经验灌入循环轮次而非清零）。
    ///     &lt;=0 时回退用 <see cref="BaseXp"/>。
    /// </summary>
    [JsonPropertyName("cycleXp")]
    public int CycleXp { get; set; }

    /// <summary>循环每轮实际所需经验（CycleXp 为正则用之，否则回退 BaseXp）。</summary>
    [JsonIgnore]
    public int CycleXpEffective => CycleXp > 0 ? CycleXp : BaseXp;

    /// <summary>返回升到 <paramref name="level"/>（从 level-1 升上来）所需经验。level 从 2 起。</summary>
    public int XpToReach(int level)
    {
        var idx = level - 2; // 升到 2 级用 curve[0]
        if (idx < 0)
        {
            return 0;
        }

        if (XpCurve.Count > idx)
        {
            return XpCurve[idx];
        }

        return BaseXp + XpGrowthPerLevel * idx;
    }
}

/// <summary>
///     物品要求（HandoverItem / FindItem / PlaceItem 类任务用）。
///     一条任务可以有多个物品要求（如同时上交若干货物），每个要求独立计数。
/// </summary>
public record BpTaskItemRequirement
{
    /// <summary>物品模板 id（tpl）。</summary>
    [JsonPropertyName("tpl")]
    public string Tpl { get; set; } = "";

    /// <summary>需找到/上交的数量。</summary>
    [JsonPropertyName("count")]
    public int Count { get; set; } = 1;

    /// <summary>展示名（仅 UI 用，可空）。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

/// <summary>
///     任务模板（管理员可编辑）。<see cref="BattlePassTrackService"/> 据此判定客户端战后上报的战绩，
///     按条件累计进度并结算 BP 经验（不注入原生 quest、不动游戏档案）。
/// </summary>
public record BpTaskTemplate
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>daily | weekly | season。</summary>
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "daily";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    /// <summary>完成奖励的 BP 经验。</summary>
    [JsonPropertyName("xp")]
    public int Xp { get; set; } = 500;

    /// <summary>fixed（固定常驻）| random（进入随机轮换池）。</summary>
    [JsonPropertyName("rotation")]
    public string Rotation { get; set; } = "random";

    /// <summary>随机池中被抽中的权重。</summary>
    [JsonPropertyName("weight")]
    public int Weight { get; set; } = 1;

    /// <summary>条件类型：Kills | Exploration | HandoverItem | FindItem | PlaceItem | VisitZone。</summary>
    [JsonPropertyName("conditionType")]
    public string ConditionType { get; set; } = "Kills";

    /// <summary>
    ///     击杀目标阵营：Any | Savage（Scav）| AnyPmc | Bear | Usec | Boss 等（对应 EFT savageRole/role 语义）。
    /// </summary>
    [JsonPropertyName("target")]
    public string Target { get; set; } = "Any";

    /// <summary>需要达成的次数（击杀数 / 到点数）。</summary>
    [JsonPropertyName("count")]
    public int Count { get; set; } = 3;

    /// <summary>
    ///     地点（EFT 内部地图 id，例如 bigmap=海关 / Sandbox=机房 / factory4_day）。空 = 不限地点。
    /// </summary>
    [JsonPropertyName("location")]
    public string? Location { get; set; }

    // ----- 细粒度击杀过滤（全部可选，留空=不限；仅 ConditionType=Kills 生效）-----
    // 对应 EFT CounterCreator 内 Kills 子条件的同名字段，多个子条件需同时满足才计数。

    /// <summary>使用的武器 tpl 列表（满足其一即可）。例：用某把枪击杀。</summary>
    [JsonPropertyName("weapons")]
    public List<string>? Weapons { get; set; }

    /// <summary>武器口径列表（如 Caliber762x39）。</summary>
    [JsonPropertyName("weaponCalibers")]
    public List<string>? WeaponCalibers { get; set; }

    /// <summary>命中部位列表（如 Head / Chest / Stomach / LeftLeg…）。</summary>
    [JsonPropertyName("bodyParts")]
    public List<string>? BodyParts { get; set; }

    /// <summary>Scav 角色细分（target=Savage 时生效，如 marksman / bossKilla / followerBully）。</summary>
    [JsonPropertyName("savageRoles")]
    public List<string>? SavageRoles { get; set; }

    /// <summary>击杀距离比较方式（>= 远距离狙杀 / &lt;= 近身），配合 distanceValue。</summary>
    [JsonPropertyName("distanceCompare")]
    public string? DistanceCompare { get; set; }

    /// <summary>击杀距离阈值（米）。</summary>
    [JsonPropertyName("distanceValue")]
    public double? DistanceValue { get; set; }

    /// <summary>时段起（游戏内小时 0-24），配合 daytimeTo 限定昼/夜击杀。</summary>
    [JsonPropertyName("daytimeFrom")]
    public int? DaytimeFrom { get; set; }

    [JsonPropertyName("daytimeTo")]
    public int? DaytimeTo { get; set; }

    /// <summary>敌人需穿戴的装备 tpl 列表（击杀穿戴指定装备的敌人）。</summary>
    [JsonPropertyName("enemyEquipment")]
    public List<string>? EnemyEquipment { get; set; }

    /// <summary>玩家自身需穿戴的装备 tpl 列表（穿戴指定装备时击杀，独立 Equipment 子条件）。</summary>
    [JsonPropertyName("playerEquipment")]
    public List<string>? PlayerEquipment { get; set; }

    /// <summary>武器上需安装的改装件 tpl 列表。</summary>
    [JsonPropertyName("weaponMods")]
    public List<string>? WeaponMods { get; set; }

    // ===== HandoverItem / FindItem / PlaceItem 类任务字段 =====

    /// <summary>物品要求列表（HandoverItem/FindItem/PlaceItem）。</summary>
    [JsonPropertyName("itemRequirements")]
    public List<BpTaskItemRequirement>? ItemRequirements { get; set; }

    /// <summary>目标区域 id（PlaceItem/VisitZone），为空则不限定区域。</summary>
    [JsonPropertyName("zoneId")]
    public string? ZoneId { get; set; }

    /// <summary>安放物品所需时间（秒，PlaceItem 用，对应 quest condition <c>plantTime</c>）。</summary>
    [JsonPropertyName("plantTime")]
    public double? PlantTime { get; set; }

    /// <summary>物品是否必须战局中找到（FindItem 用，对应 <c>onlyFoundInRaid</c>）；HandoverItem 默认消耗。</summary>
    [JsonPropertyName("findInRaid")]
    public bool FindInRaid { get; set; }

    /// <summary>
    ///     是否要求「单局内完成」。
    ///     <c>false</c>（默认）：进度<b>跨局累计</b>，完一局不清零，仅在 daily/weekly/season 轮换时归零。
    ///     <c>true</c>：需单局内一次性达标（如「单局击杀 5 个」），本场不达标则进度按场重置为本场值，不跨局累加。
    /// </summary>
    [JsonPropertyName("singleRaid")]
    public bool SingleRaid { get; set; }
}

/// <summary>激活码。type=premium 解锁付费轨；type=levels 直升 value 级。</summary>
public record BpActivationCode
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    /// <summary>premium | levels。</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "premium";

    /// <summary>type=levels 时直升的等级数；premium 时忽略。</summary>
    [JsonPropertyName("value")]
    public int Value { get; set; }

    /// <summary>批次标签（便于管理员分发归类）。</summary>
    [JsonPropertyName("batchTag")]
    public string? BatchTag { get; set; }

    [JsonPropertyName("createdUtc")]
    public long CreatedUtc { get; set; }

    [JsonPropertyName("redeemedBy")]
    public string? RedeemedBy { get; set; }

    [JsonPropertyName("redeemedUtc")]
    public long? RedeemedUtc { get; set; }
}

/// <summary>玩家某赛季内的一条活跃任务实例。</summary>
public record BpActiveTask
{
    [JsonPropertyName("taskId")]
    public string TaskId { get; set; } = "";

    /// <summary>历史遗留字段（旧版注入的 quest id）；客户端追踪方案下不再使用，保留以兼容旧存档反序列化。</summary>
    [JsonPropertyName("questId")]
    public string QuestId { get; set; } = "";

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "daily";

    [JsonPropertyName("acceptedUtc")]
    public long AcceptedUtc { get; set; }

    /// <summary>当前进度值（来自客户端战后上报累计；singleRaid=false 时跨局累加，轮换时归零）。</summary>
    [JsonPropertyName("progress")]
    public int Progress { get; set; }

    /// <summary>是否已把该任务的 BP 经验结算入账（幂等标记）。</summary>
    [JsonPropertyName("creditedXp")]
    public bool CreditedXp { get; set; }
}

/// <summary>玩家通行证进度（每 profile 一份，按赛季重置）。</summary>
public record BpProgress
{
    [JsonPropertyName("seasonId")]
    public string SeasonId { get; set; } = "";

    [JsonPropertyName("level")]
    public int Level { get; set; } = 1;

    [JsonPropertyName("xp")]
    public int Xp { get; set; }

    [JsonPropertyName("premiumUnlocked")]
    public bool PremiumUnlocked { get; set; }

    /// <summary>已领取的免费轨等级。</summary>
    [JsonPropertyName("claimedFree")]
    public HashSet<int> ClaimedFree { get; set; } = new();

    /// <summary>已领取的付费轨等级。</summary>
    [JsonPropertyName("claimedPremium")]
    public HashSet<int> ClaimedPremium { get; set; } = new();

    /// <summary>满级后已完成的循环奖励轮次数（每轮消耗 <see cref="BpSeason.CycleXpEffective"/> 经验）。</summary>
    [JsonPropertyName("cyclesCompleted")]
    public int CyclesCompleted { get; set; }

    /// <summary>已领取的免费轨循环轮次（轮次序号从 1 起）。</summary>
    [JsonPropertyName("claimedCycleFree")]
    public HashSet<int> ClaimedCycleFree { get; set; } = new();

    /// <summary>已领取的付费轨循环轮次。</summary>
    [JsonPropertyName("claimedCyclePremium")]
    public HashSet<int> ClaimedCyclePremium { get; set; } = new();

    [JsonPropertyName("activeTasks")]
    public List<BpActiveTask> ActiveTasks { get; set; } = new();

    [JsonPropertyName("lastDailyRollUtc")]
    public long LastDailyRollUtc { get; set; }

    [JsonPropertyName("lastWeeklyRollUtc")]
    public long LastWeeklyRollUtc { get; set; }

    /// <summary>当日已用的免费刷新次数（按 lastDailyRollUtc 所在自然日重置）。</summary>
    [JsonPropertyName("dailyRerollsUsed")]
    public int DailyRerollsUsed { get; set; }

    /// <summary>已处理（已结束/已切换）的客户端上报 raidId；用于拦截已收尾战局的乱序迟到上报。仅保留最近若干条。</summary>
    [JsonPropertyName("processedRaidIds")]
    public List<string> ProcessedRaidIds { get; set; } = new();

    /// <summary>
    ///     实时增量上报：当前正在累计的战局 raidId。客户端每次事件上报「累计快照」，
    ///     服务端据此识别是否为同一局；切换到新 raidId 时归档旧局并清空 <see cref="CurrentRaidApplied"/>。
    /// </summary>
    [JsonPropertyName("currentRaidId")]
    public string? CurrentRaidId { get; set; }

    /// <summary>
    ///     当前战局内每条任务「已应用到 Progress 的本场累计值」（taskId → 已应用量）。
    ///     收到新快照时，仅把 (本场最新 delta - 已应用量) 这部分增量补进 Progress，实现累计快照的幂等增量应用。
    /// </summary>
    [JsonPropertyName("currentRaidApplied")]
    public Dictionary<string, int> CurrentRaidApplied { get; set; } = new();
}

// ============================================================================
//  客户端任务追踪上报（客户端 BepInEx 插件 → POST /battlepass/api/track）
//  客户端只上报「原始战绩事件」，条件匹配/累计/结算全在服务端（BattlePassTrackService）。
// ============================================================================

/// <summary>一次击杀事件：阵营 + 角色（服务端据此匹配任务 target）。</summary>
public record BpKillEvent
{
    [JsonPropertyName("side")]
    public string? Side { get; set; }

    [JsonPropertyName("role")]
    public string? Role { get; set; }
}

/// <summary>一项物品事件（找到/带出/安放）。</summary>
public record BpTrackItem
{
    [JsonPropertyName("tpl")]
    public string Tpl { get; set; } = "";

    [JsonPropertyName("count")]
    public int Count { get; set; } = 1;

    /// <summary>安放区域（PlaceItem 用，可空）。</summary>
    [JsonPropertyName("zoneId")]
    public string? ZoneId { get; set; }
}

/// <summary>客户端战局结束一次性上报的战绩载荷。</summary>
public record RaidTrackPayload
{
    /// <summary>本场唯一 id（客户端生成）；服务端按它幂等去重。</summary>
    [JsonPropertyName("raidId")]
    public string RaidId { get; set; } = "";

    /// <summary>本场地图（EFT 内部地图 id）。</summary>
    [JsonPropertyName("location")]
    public string? Location { get; set; }

    /// <summary>撤离状态（Survived / Killed / Left / Runner ...）。</summary>
    [JsonPropertyName("exitStatus")]
    public string? ExitStatus { get; set; }

    [JsonPropertyName("kills")]
    public List<BpKillEvent> Kills { get; set; } = new();

    [JsonPropertyName("visitedZones")]
    public List<string> VisitedZones { get; set; } = new();

    [JsonPropertyName("foundItems")]
    public List<BpTrackItem> FoundItems { get; set; } = new();

    [JsonPropertyName("placedItems")]
    public List<BpTrackItem> PlacedItems { get; set; } = new();
}

// ============================================================================
//  通行证商人（Battle Pass Trader）
//  商人元信息 + 货架 offer 均可后台图形化配置；货架按「网页领取购买权限」逐玩家解锁。
//  落盘：SPT_Data/battlepass/trader-meta.json（元信息）、trader.json（offers）。
// ============================================================================

/// <summary>以物易物价格的一项：用某 tpl 物品若干换购。</summary>
public record BpBarterCost
{
    /// <summary>支付物品模板 id（卢布 5449016a4bdc2d6f028b456f / 任意物品 / 代币）。</summary>
    [JsonPropertyName("tpl")]
    public string Tpl { get; set; } = "";

    /// <summary>支付数量。</summary>
    [JsonPropertyName("count")]
    public int Count { get; set; } = 1;
}

/// <summary>商人货架上的一个商品项（后台可增删改）。</summary>
public record BpTraderOffer
{
    /// <summary>稳定 id（被通行证奖励 purchaseRight.offerId 引用，也用于派生货架根 item / 解锁 quest）。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>出售的物品模板 id（tpl）。</summary>
    [JsonPropertyName("tpl")]
    public string Tpl { get; set; } = "";

    /// <summary>展示名（仅 UI 用，可空）。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>库存/可售总量（&lt;=0 = 无限）。</summary>
    [JsonPropertyName("stock")]
    public int Stock { get; set; } = -1;

    /// <summary>每个玩家限购数（&lt;=0 = 不限）。</summary>
    [JsonPropertyName("buyLimit")]
    public int BuyLimit { get; set; }

    /// <summary>以物易物价格（同一组内多项需同时支付；为空 = 免费 0 价）。</summary>
    [JsonPropertyName("cost")]
    public List<BpBarterCost> Cost { get; set; } = new();
}

/// <summary>商人自身可配置元信息（名称/昵称/介绍/货币/头像等）。</summary>
public record BpTraderConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "通行证商人";

    [JsonPropertyName("nickname")]
    public string Nickname { get; set; } = "Pass";

    [JsonPropertyName("surname")]
    public string? Surname { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = "凭通行证解锁的购买权限，在此兑现你的专属补给。";

    /// <summary>商人「所在地」展示文案。</summary>
    [JsonPropertyName("location")]
    public string Location { get; set; } = "通行证";

    /// <summary>货币类型：RUB | USD | EUR | GP（影响商人主货币展示）。</summary>
    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "RUB";

    /// <summary>是否默认解锁（无需前置任务即可见）。</summary>
    [JsonPropertyName("unlockedByDefault")]
    public bool UnlockedByDefault { get; set; } = true;

    /// <summary>补货间隔（秒）。</summary>
    [JsonPropertyName("resupplySeconds")]
    public int ResupplySeconds { get; set; } = 3600;

    /// <summary>是否提供保险。</summary>
    [JsonPropertyName("insuranceAvailable")]
    public bool InsuranceAvailable { get; set; }

    /// <summary>是否提供维修。</summary>
    [JsonPropertyName("repairAvailable")]
    public bool RepairAvailable { get; set; }

    /// <summary>头像文件名（位于 SPT_Data/battlepass/ 下；空 = 用内置默认头像）。</summary>
    [JsonPropertyName("avatarFile")]
    public string? AvatarFile { get; set; }
}

// ============================================================================
//  通行证称号（Battle Pass Titles）
//  称号是永久身份徽章，分文字（可单色/双色渐变）与图片（约定 128×32 PNG 透明底）两种形态。
//  目录由管理员定义；玩家「拥有/佩戴」存独立 titles/{profileId}.json（不进 BpProgress，跨赛季保留）。
//  对外经 HTTP 公开只读端点 + C# 静态辅助（BattlePassTitleApi）供其他 mod（如 Fika）查询并渲染。
//  落盘：SPT_Data/battlepass/titles.json（目录）、titles/{profileId}.json（玩家）、titles-img/{id}.png（图片）。
// ============================================================================

/// <summary>称号目录项（管理员定义，全局共享）。</summary>
public record BpTitle
{
    /// <summary>稳定 id（被奖励 <see cref="BpReward.TitleId"/> 与玩家 owned 引用，也用作图片文件名/路由）。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>管理员标签（目录/授予列表展示用，可与展示文本不同）。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>形态：text（文字）| image（图片）。</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    // ----- 文字形态 -----

    /// <summary>显示文本（type=text）。</summary>
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    /// <summary>主色 #RRGGBB（type=text）。</summary>
    [JsonPropertyName("color")]
    public string? Color { get; set; }

    /// <summary>可选第二色 #RRGGBB；非空时与 <see cref="Color"/> 做线性渐变（type=text）。</summary>
    [JsonPropertyName("colorEnd")]
    public string? ColorEnd { get; set; }

    // ----- 图片形态 -----

    /// <summary>图片文件名（type=image，位于 titles-img/ 下，约定 {id}.png）。</summary>
    [JsonPropertyName("imageFile")]
    public string? ImageFile { get; set; }

    /// <summary>图片宽（约定 128）。</summary>
    [JsonPropertyName("width")]
    public int Width { get; set; } = 128;

    /// <summary>图片高（约定 32）。</summary>
    [JsonPropertyName("height")]
    public int Height { get; set; } = 32;

    /// <summary>说明（仅 UI 用，可空）。</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

/// <summary>玩家拥有 / 佩戴的称号（每 profile 一份，跨赛季持久，独立于 BpProgress）。</summary>
public record BpPlayerTitles
{
    /// <summary>已拥有的称号 id 集合。</summary>
    [JsonPropertyName("owned")]
    public HashSet<string> Owned { get; set; } = new();

    /// <summary>当前佩戴的称号 id（null/空 = 未佩戴）。</summary>
    [JsonPropertyName("equipped")]
    public string? Equipped { get; set; }
}

/// <summary>
///     对外暴露的称号视图（公开 HTTP 接口与 <c>BattlePassTitleApi</c> 共用）。
///     调用方（Fika 等）据此渲染：type=text 用 color/colorEnd 着色（单色或线性渐变），
///     type=image 直接贴 imageUrl（约定 128×32，可按 UI 缩放）。
/// </summary>
public sealed record BpTitleView
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "text";

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("color")]
    public string? Color { get; init; }

    [JsonPropertyName("colorEnd")]
    public string? ColorEnd { get; init; }

    [JsonPropertyName("imageUrl")]
    public string? ImageUrl { get; init; }

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }
}
