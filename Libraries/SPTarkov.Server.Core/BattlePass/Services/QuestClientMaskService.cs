using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.BattlePass.ItemControl;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>
///     原版任务「禁用」的服务期屏蔽索引（最小破坏：屏蔽而非删除）。
///     从 <c>quest-overrides.json</c> 的 <see cref="BpQuestOverride.Disabled"/> 条目构建禁用 questId 集，
///     供 <c>QuestHelper</c> 的下发链路（GetClientQuests / GetNewlyAccessibleQuestsWhenStartingQuest）
///     在返回前过滤，从而无需破坏式改动 5.6MB 内存 DB。索引仅在 <see cref="Rebuild"/>（启动 + 后台编辑后由
///     <see cref="QuestSync.Sync"/> 调用）时重建，热路径查询纯内存、零磁盘 IO。
/// </summary>
[Injectable(InjectionType.Singleton)]
public class QuestClientMaskService(ISptLogger<QuestClientMaskService> logger)
{
    private HashSet<MongoId> _disabled = new();
    private volatile bool _any;

    /// <summary>是否存在任一被禁用任务（无则下发链路可零开销跳过过滤）。</summary>
    public bool HasAny => _any;

    /// <summary>从 override 存储重建禁用索引（仅在启动 / 后台编辑后调用，非热路径）。</summary>
    public void Rebuild()
    {
        var disabled = new HashSet<MongoId>();
        try
        {
            foreach (var ov in BattlePassStore.GetQuestOverrides())
            {
                if (ov.Disabled && MongoIdEx.TryParse(ov.QuestId, out var questId))
                {
                    disabled.Add(questId);
                }
            }
        }
        catch (Exception ex)
        {
            logger.Error($"[SPT-BattlePass] 任务禁用索引重建失败: {ex.Message}");
        }

        _disabled = disabled;
        _any = disabled.Count > 0;
    }

    /// <summary>该任务是否被后台禁用（应从下发给客户端的任务表剔除）。</summary>
    public bool IsQuestDisabled(MongoId questId)
    {
        return _any && _disabled.Contains(questId);
    }
}
