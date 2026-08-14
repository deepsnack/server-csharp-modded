using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;

namespace SPTarkov.Server.Core.Models.Eft.Profile;

/// <summary>PMC 统计摘要（单 Key 匹配 <see cref="OverallCounters.Items"/>），供统计 API 不物化整档。</summary>
public record StatsSummary
{
    public double Kills { get; init; }
    public double Deaths { get; init; }
    public double AmmoUsed { get; init; }
    public double BodyDamage { get; init; }
    public double ArmorDamage { get; init; }
    public double Headshots { get; init; }
    public double BossKills { get; init; }

    public static StatsSummary? FromOverallCounters(List<CounterKeyValue>? items)
    {
        if (items is not { Count: > 0 })
        {
            return null;
        }

        return new StatsSummary
        {
            Kills = GetSingleKeyCounter(items, "Kills"),
            Deaths = GetSingleKeyCounter(items, "Deaths"),
            AmmoUsed = GetSingleKeyCounter(items, "AmmoUsed"),
            BodyDamage = GetSingleKeyCounter(items, "CauseBodyDamage"),
            ArmorDamage = GetSingleKeyCounter(items, "CauseArmorDamage"),
            Headshots = GetSingleKeyCounter(items, "HeadShots"),
            BossKills = GetSingleKeyCounter(items, "KilledBoss"),
        };
    }

    private static double GetSingleKeyCounter(List<CounterKeyValue> items, string key)
    {
        var match = items.FirstOrDefault(x => x.Key?.Count == 1 && x.Key.Contains(key));
        return match?.Value ?? 0.0;
    }
}

/// <summary>
///     轻量存档头索引（原 SPT-ProfileCore LazyProfile 内置化）。
///     懒加载开启时启动只用 JsonDocument 抽取这些字段，不反序列化整档；
///     供身份显示（日志后缀）、离线清理、按需物化使用。
/// </summary>
public class LazyProfileHeader
{
    public required Info ProfileInfo { get; init; }
    public string? Nickname { get; init; }
    public int? Level { get; init; }
    public string? Side { get; init; }
    public int? Experience { get; init; }
    public MongoId? PmcId { get; init; }
    public int? PmcAid { get; init; }
    public MemberCategory? MemberCategory { get; init; }
    public MemberCategory? SelectedMemberCategory { get; init; }
    public Spt? SptData { get; init; }

    /// <summary>PMC 已完成的成就 ID；用于全服成就统计而不物化整档。</summary>
    public IReadOnlySet<MongoId> AchievementIds { get; init; } = new HashSet<MongoId>();

    /// <summary>该档是否有跳蚤在售挂单（启动恢复市场时只物化有挂单的档）。</summary>
    public bool HasRagfairOffers { get; init; }

    /// <summary>PMC 统计摘要（最近一次成功保存的值；供 Fika 统计 API 使用，不物化整档）。</summary>
    public StatsSummary? StatsSummary { get; init; }

    /// <summary>是否存在 RagFair 禁令记录（等价于 Info.Bans?.Any(x => x.BanType is BanType.RagFair)）。</summary>
    public bool HasRagfairBan { get; init; }

    /// <summary>profile JSON 文件完整路径（含扩展名；兼容 MongoId 与用户名两种命名）。</summary>
    public required string FilePath { get; init; }

    public bool IsLoaded { get; set; }
    public bool IsInvalid { get; set; }
}
