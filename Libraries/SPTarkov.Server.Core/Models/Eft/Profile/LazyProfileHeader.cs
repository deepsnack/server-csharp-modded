using SPTarkov.Server.Core.Models.Common;

namespace SPTarkov.Server.Core.Models.Eft.Profile;

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

    /// <summary>该档是否有跳蚤在售挂单（启动恢复市场时只物化有挂单的档）。</summary>
    public bool HasRagfairOffers { get; init; }

    /// <summary>profile JSON 文件完整路径（含扩展名；兼容 MongoId 与用户名两种命名）。</summary>
    public required string FilePath { get; init; }

    public bool IsLoaded { get; set; }
    public bool IsInvalid { get; set; }
}
