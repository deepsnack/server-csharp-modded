using System.Collections.Frozen;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;

namespace SPTarkov.Server.Core.BattlePass.ItemControl;

/// <summary>
///     地图战利品倒排索引：启动期一次性物化各 Location 的 loot，建 <c>tpl → LootSourceRef[]</c>
///     索引（<see cref="FrozenDictionary{TKey,TValue}"/>），随后释放物化数据（GC）。之后查询 O(1)。
///     <para>
///       索引为「原始」快照（在本 mod 的 loot 编辑 transformer 注册之前于 +89000 构建，早于
///       <see cref="ItemControlSync"/> 的 +90000）；本 mod 的增删 loot 改动在 <see cref="GetLootSources"/>
///       查询期叠加当前 override，故运行时编辑也即时反映、索引保持不可变。
///     </para>
/// </summary>
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostDBModLoader + 89000)]
public class LootIndexService(DatabaseService databaseService, ISptLogger<LootIndexService> logger) : IOnLoad
{
    private FrozenDictionary<MongoId, List<LootSourceRef>> _index =
        FrozenDictionary<MongoId, List<LootSourceRef>>.Empty;

    public Task OnLoad()
    {
        try
        {
            var build = new Dictionary<MongoId, List<LootSourceRef>>();

            void Add(MongoId tpl, string locationId, string kind, string? cos)
            {
                if (!build.TryGetValue(tpl, out var list))
                {
                    list = new List<LootSourceRef>();
                    build[tpl] = list;
                }

                // 去重（同 location+kind+容器只记一次）
                if (!list.Any(r => r.LocationId == locationId && r.Kind == kind && r.ContainerOrSpawn == cos))
                {
                    list.Add(new LootSourceRef { LocationId = locationId, Kind = kind, ContainerOrSpawn = cos });
                }
            }

            foreach (var (name, loc) in databaseService.GetLocations().GetDictionary())
            {
                if (loc is null)
                {
                    continue;
                }

                // 静态容器分布
                try
                {
                    var staticLoot = loc.StaticLoot?.Value;
                    if (staticLoot is not null)
                    {
                        foreach (var (containerTpl, details) in staticLoot)
                        {
                            foreach (var dist in details.ItemDistribution ?? Enumerable.Empty<ItemDistribution>())
                            {
                                Add(dist.Tpl, name, "static", containerTpl.ToString());
                            }
                        }
                    }
                }
                catch { /* 单图损坏不影响整体 */ }

                // 强制容器物品
                try
                {
                    var containers = loc.StaticContainers?.Value;
                    if (containers?.StaticForced is not null)
                    {
                        foreach (var forced in containers.StaticForced)
                        {
                            Add(forced.ItemTpl, name, "forced", forced.ContainerId);
                        }
                    }
                }
                catch { }

                // 散落 loot（取每个散落点的根物品 tpl）
                try
                {
                    var loose = loc.LooseLoot?.Value;
                    if (loose is not null)
                    {
                        foreach (var sp in (loose.Spawnpoints ?? Enumerable.Empty<Spawnpoint>())
                                 .Concat(loose.SpawnpointsForced ?? Enumerable.Empty<Spawnpoint>()))
                        {
                            var tpl = RootTpl(sp);
                            if (tpl.HasValue)
                            {
                                Add(tpl.Value, name, "loose", sp.Template?.Id);
                            }
                        }
                    }
                }
                catch { }

                // 静态弹药
                try
                {
                    foreach (var (caliber, ammos) in loc.StaticAmmo ?? new Dictionary<string, IEnumerable<StaticAmmoDetails>>())
                    {
                        foreach (var a in ammos)
                        {
                            if (a.Tpl.HasValue)
                            {
                                Add(a.Tpl.Value, name, "ammo", caliber);
                            }
                        }
                    }
                }
                catch { }
            }

            _index = build.ToFrozenDictionary();
            logger.Success($"[SPT-BattlePass] 战利品索引已建立：覆盖 {_index.Count} 种物品。");
        }
        catch (Exception ex)
        {
            logger.Error($"[SPT-BattlePass] 战利品索引建立失败: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private static MongoId? RootTpl(Spawnpoint sp)
    {
        var items = sp.Template?.Items;
        if (items is null)
        {
            return null;
        }

        var root = sp.Template?.Root;
        var match = root is not null ? items.FirstOrDefault(i => i.Id.ToString() == root) : items.FirstOrDefault();
        return match?.Template;
    }

    /// <summary>查询某 tpl 的战利品来源（原始索引 + 当前 loot override 叠加）。</summary>
    public List<LootSourceRef> GetLootSources(MongoId tpl)
    {
        var result = _index.TryGetValue(tpl, out var baseList)
            ? new List<LootSourceRef>(baseList)
            : new List<LootSourceRef>();

        var tplStr = tpl.ToString();
        foreach (var ov in BattlePassStore.GetItemOverrides())
        {
            if (ov.Source != AcqSource.Loot || ov.Tpl != tplStr)
            {
                continue;
            }

            if (ov.Op == "remove")
            {
                result.RemoveAll(r =>
                    r.LocationId.Equals(ov.LocationId, StringComparison.OrdinalIgnoreCase)
                    && (ov.ContainerOrSpawn is null || r.ContainerOrSpawn == ov.ContainerOrSpawn));
            }
            else if (ov.Op == "add" && ov.LocationId is not null)
            {
                result.Add(new LootSourceRef
                {
                    LocationId = ov.LocationId,
                    Kind = ov.LootKind,
                    ContainerOrSpawn = ov.ContainerOrSpawn,
                });
            }
        }

        return result;
    }

    public bool Ready => _index.Count > 0;
}
