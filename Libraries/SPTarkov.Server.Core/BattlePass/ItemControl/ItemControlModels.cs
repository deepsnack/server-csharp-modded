using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Common;

namespace SPTarkov.Server.Core.BattlePass.ItemControl;

/// <summary>MongoId 缺少 TryParse；用 IsValidMongoId + 隐式构造补一个，供本子域各处复用。</summary>
internal static class MongoIdEx
{
    public static bool TryParse(string? s, out MongoId id)
    {
        if (!string.IsNullOrEmpty(s) && MongoId.IsValidMongoId(s))
        {
            id = new MongoId(s);
            return true;
        }

        id = default;
        return false;
    }
}

// ============================================================================
//  物品管控（Item Control）数据模型
//  两大能力：跳蚤黑名单接管（flea-control.json）+ 物品获取途径编辑（item-overrides.json）。
//  全部 mod 本地 record；落盘 SPT_Data/battlepass/ 下。
// ============================================================================

/// <summary>获取来源类型。</summary>
public static class AcqSource
{
    public const string Trader = "trader";
    public const string Quest = "quest";
    public const string Hideout = "hideout";
    public const string StartInv = "startInv";
    public const string Loot = "loot";
}

/// <summary>以物易物支付项（复用通行证商人的语义：tpl + 数量）。</summary>
public record BpItemCost
{
    [JsonPropertyName("tpl")]
    public string Tpl { get; set; } = "";

    [JsonPropertyName("count")]
    public int Count { get; set; } = 1;
}

/// <summary>
///     一条获取途径编辑操作（持久化，启动重放 + 运行时 Sync 幂等应用）。
///     按 <see cref="Source"/> 取用不同定位字段；<see cref="Op"/> = add | remove。
/// </summary>
public record BpItemOverride
{
    /// <summary>稳定 id（用于去重/删除该 override 本身）。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>add | remove。</summary>
    [JsonPropertyName("op")]
    public string Op { get; set; } = "remove";

    /// <summary>trader | quest | hideout | startInv | loot（见 <see cref="AcqSource"/>）。</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    /// <summary>目标物品模板 id。</summary>
    [JsonPropertyName("tpl")]
    public string Tpl { get; set; } = "";

    /// <summary>数量（add 时用：奖励/初始库存/loot 堆叠等；默认 1）。</summary>
    [JsonPropertyName("count")]
    public int Count { get; set; } = 1;

    // ---- 商人 ----
    [JsonPropertyName("traderId")]
    public string? TraderId { get; set; }

    /// <summary>忠诚等级（add 商人货架时，默认 1）。</summary>
    [JsonPropertyName("loyalty")]
    public int Loyalty { get; set; } = 1;

    /// <summary>以物易物价格（add 商人货架时；为空=按 handbook 卢布价或免费）。</summary>
    [JsonPropertyName("cost")]
    public List<BpItemCost>? Cost { get; set; }

    // ---- 任务 ----
    [JsonPropertyName("questId")]
    public string? QuestId { get; set; }

    /// <summary>奖励组：Started | Success | Fail | AvailableForFinish（默认 Success）。</summary>
    [JsonPropertyName("rewardGroup")]
    public string RewardGroup { get; set; } = "Success";

    // ---- 藏身处 ----
    /// <summary>制造配方 id（remove 原料 / add 原料到现有配方时定位；为空且 role=output 时表示新建简易配方）。</summary>
    [JsonPropertyName("recipeId")]
    public string? RecipeId { get; set; }

    /// <summary>藏身处编辑角色：output（产物）| ingredient（原料）。</summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = "output";

    // ---- 初始库存 ----
    /// <summary>阵营：Usec | Bear | both（默认 both）。</summary>
    [JsonPropertyName("side")]
    public string Side { get; set; } = "both";

    // ---- 战利品 ----
    [JsonPropertyName("locationId")]
    public string? LocationId { get; set; }

    /// <summary>战利品种类：static（容器静态）| loose（散落）| forced（强制）。</summary>
    [JsonPropertyName("lootKind")]
    public string LootKind { get; set; } = "static";

    /// <summary>容器 tpl 或散落点 id（loot 定位用，可空=按 location 粒度）。</summary>
    [JsonPropertyName("containerOrSpawn")]
    public string? ContainerOrSpawn { get; set; }

    /// <summary>相对权重（add loot 时，默认 1）。</summary>
    [JsonPropertyName("weight")]
    public int Weight { get; set; } = 1;
}

/// <summary>跳蚤市场黑名单接管配置。</summary>
public record BpFleaControl
{
    /// <summary>按 tpl 拉黑（加入 RagfairConfig.Dynamic.Blacklist.Custom）。</summary>
    [JsonPropertyName("blacklistTpls")]
    public HashSet<string> BlacklistTpls { get; set; } = new();

    /// <summary>白名单：强制放开（设 _props.CanSellOnRagfair=true，并确保不在 Custom 内）。</summary>
    [JsonPropertyName("whitelistTpls")]
    public HashSet<string> WhitelistTpls { get; set; } = new();

    /// <summary>按父类/分类拉黑（加入 CustomItemCategoryList）。</summary>
    [JsonPropertyName("blacklistCategories")]
    public HashSet<string> BlacklistCategories { get; set; } = new();

    // ---- 原生开关（直接写回 RagfairConfig.Dynamic.Blacklist 同名字段；null=不接管该开关，保持原值）----
    [JsonPropertyName("enableBsgList")]
    public bool? EnableBsgList { get; set; }

    [JsonPropertyName("enableQuestList")]
    public bool? EnableQuestList { get; set; }

    [JsonPropertyName("traderItems")]
    public bool? TraderItems { get; set; }

    [JsonPropertyName("damagedAmmoPacks")]
    public bool? DamagedAmmoPacks { get; set; }

    [JsonPropertyName("enableCustomItemCategoryList")]
    public bool? EnableCustomItemCategoryList { get; set; }
}

// ---- 对外只读视图（搜索 / 获取图谱） ----

/// <summary>物品搜索结果项。</summary>
public sealed record BpItemSearchHit
{
    [JsonPropertyName("tpl")]
    public string Tpl { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("shortName")]
    public string? ShortName { get; init; }

    [JsonPropertyName("parent")]
    public string Parent { get; init; } = "";

    [JsonPropertyName("isMod")]
    public bool IsMod { get; init; }

    [JsonPropertyName("canSellOnRagfair")]
    public bool CanSellOnRagfair { get; init; }
}

/// <summary>战利品来源引用（倒排索引条目，轻量）。</summary>
public sealed record LootSourceRef
{
    [JsonPropertyName("locationId")]
    public string LocationId { get; init; } = "";

    /// <summary>static | loose | forced | ammo。</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    /// <summary>容器 tpl 或散落点/槽位标识（可空）。</summary>
    [JsonPropertyName("containerOrSpawn")]
    public string? ContainerOrSpawn { get; init; }
}

/// <summary>单条获取途径（图谱对外视图）。</summary>
public sealed record BpAcquisitionEntry
{
    /// <summary>trader | quest | hideout | startInv | loot。</summary>
    [JsonPropertyName("source")]
    public string Source { get; init; } = "";

    /// <summary>人类可读摘要（如「治疗者 LL2 货架」「任务 X 的 Success 奖励」）。</summary>
    [JsonPropertyName("label")]
    public string Label { get; init; } = "";

    /// <summary>结构化定位（traderId/questId/recipeId/locationId 等，供编辑回填）。</summary>
    [JsonPropertyName("ref")]
    public Dictionary<string, string?> Ref { get; init; } = new();

    /// <summary>是否由本 mod 的 override 新增（add）。</summary>
    [JsonPropertyName("fromOverride")]
    public bool FromOverride { get; init; }
}

/// <summary>某任务的前置路径节点（任务路径展示）。</summary>
public sealed record BpQuestPathNode
{
    [JsonPropertyName("questId")]
    public string QuestId { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>该任务的直接前置 questId 列表。</summary>
    [JsonPropertyName("prerequisites")]
    public List<string> Prerequisites { get; init; } = new();
}

/// <summary>物品获取图谱（聚合结果）。</summary>
public sealed record BpAcquisitionGraph
{
    [JsonPropertyName("tpl")]
    public string Tpl { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("parent")]
    public string Parent { get; init; } = "";

    [JsonPropertyName("isMod")]
    public bool IsMod { get; init; }

    [JsonPropertyName("canSellOnRagfair")]
    public bool CanSellOnRagfair { get; init; }

    [JsonPropertyName("fleaBlacklisted")]
    public bool FleaBlacklisted { get; init; }

    [JsonPropertyName("entries")]
    public List<BpAcquisitionEntry> Entries { get; init; } = new();

    /// <summary>涉及到的任务的前置路径（按 questId）。</summary>
    [JsonPropertyName("questPaths")]
    public List<BpQuestPathNode> QuestPaths { get; init; } = new();
}
