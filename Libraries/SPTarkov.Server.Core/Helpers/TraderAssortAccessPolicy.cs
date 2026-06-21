using System.Collections.Concurrent;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace SPTarkov.Server.Core.Helpers;

/// <summary>
///     商人货架的附加访问策略扩展点。使用静态注册避免改变 TraderAssortHelper 构造签名，
///     保持对继承该类型的第三方 mod 兼容。
/// </summary>
public static class TraderAssortAccessPolicy
{
    public delegate TraderAssort Filter(MongoId sessionId, MongoId traderId, TraderAssort assort, bool isFlea);

    private static readonly ConcurrentDictionary<string, Filter> Filters = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(string key, Filter filter)
    {
        Filters[key] = filter;
    }

    public static TraderAssort Apply(MongoId sessionId, MongoId traderId, TraderAssort assort, bool isFlea)
    {
        foreach (var filter in Filters.Values)
        {
            assort = filter(sessionId, traderId, assort, isFlea);
        }

        return assort;
    }
}
