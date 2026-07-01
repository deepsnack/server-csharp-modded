using System.Collections.Concurrent;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>任务目标跳过的服务端权威实现：查询、校验、扣券、改档、保存及失败回滚。</summary>
[Injectable(InjectionType.Singleton)]
public class QuestSkipService(
    ProfileHelper profileHelper,
    DatabaseService databaseService,
    BattlePassStashService stashService,
    ProfileActivityService profileActivityService,
    QuestSkipChineseService chineseService,
    QuestSkipProfileSaveService profileSaveService,
    ICloner cloner,
    TimeUtil timeUtil,
    ISptLogger<QuestSkipService> logger
)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> profileLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Dictionary<string, QuestSkipTarget>> actionMaps = new(StringComparer.OrdinalIgnoreCase);

    public QuestSkipStateResult GetState(string profileId)
    {
        if (!MongoId.IsValidMongoId(profileId))
        {
            return new QuestSkipStateResult { Success = false, Message = "会话无效，请重新登录" };
        }

        var gate = profileLocks.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            var sessionId = new MongoId(profileId);
            var inRaid = profileActivityService.IsRaidActive(sessionId);
            var pmc = profileHelper.GetPmcProfile(sessionId);
            if (pmc is null)
            {
                return new QuestSkipStateResult { Success = false, Message = "未找到玩家存档" };
            }

            var ticketCount = stashService.CountTpl(pmc, QuestSkipTicketService.TicketTpl.ToString(), false);
            var actions = new Dictionary<string, QuestSkipTarget>(StringComparer.Ordinal);
            var tasks = new List<QuestSkipTaskView>();
            foreach (var questStatus in pmc.Quests ?? [])
            {
                if (questStatus.Status != QuestStatusEnum.Started)
                {
                    continue;
                }

                var resolved = ResolveQuest(pmc, questStatus.QId);
                if (resolved is null)
                {
                    logger.Warning($"[SPT-BattlePass] 跳过任务展示忽略缺失定义 profile={profileId} quest={questStatus.QId}");
                    continue;
                }

                var topLevel = (resolved.Quest.Conditions.AvailableForFinish ?? [])
                    .Where(condition => string.IsNullOrWhiteSpace(condition.ParentId))
                    .OrderBy(condition => condition.Index ?? int.MaxValue)
                    .ToList();
                if (topLevel.Count == 0)
                {
                    continue;
                }

                var completedConditions = new HashSet<string>(questStatus.CompletedConditions ?? [], StringComparer.OrdinalIgnoreCase);
                var objectives = new List<QuestSkipObjectiveView>();
                foreach (var condition in topLevel)
                {
                    var conditionId = condition.Id.ToString();
                    var completed = completedConditions.Contains(conditionId);
                    string? actionId = null;
                    if (!completed)
                    {
                        actionId = Guid.NewGuid().ToString("N");
                        actions[actionId] = new QuestSkipTarget(questStatus.QId, condition.Id);
                    }

                    objectives.Add(new QuestSkipObjectiveView
                    {
                        ActionId = actionId,
                        ConditionNameZh = chineseService.ConditionName(condition),
                        ConditionDescriptionZh = chineseService.ConditionDescription(condition, resolved.IsRepeatable),
                        Completed = completed,
                        CanSkip = !completed && !inRaid && ticketCount > 0,
                    });
                }

                tasks.Add(new QuestSkipTaskView
                {
                    TitleZh = chineseService.QuestTitle(resolved.Quest, resolved.IsRepeatable, resolved.RepeatableGroupName),
                    Objectives = objectives,
                });
            }

            actionMaps[profileId] = actions;
            return new QuestSkipStateResult
            {
                Success = true,
                TicketCount = ticketCount,
                InRaid = inRaid,
                Tasks = tasks,
            };
        }
        catch (Exception ex)
        {
            logger.Error($"[SPT-BattlePass] 读取任务跳过状态失败 profile={profileId}: {ex}");
            return new QuestSkipStateResult { Success = false, Message = "读取任务失败，请稍后重试" };
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<QuestSkipResult> SkipAsync(string profileId, string? actionId)
    {
        if (!MongoId.IsValidMongoId(profileId) || string.IsNullOrWhiteSpace(actionId))
        {
            return Failure("任务目标无效，请刷新页面");
        }

        var gate = profileLocks.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (!actionMaps.TryGetValue(profileId, out var actions) || !actions.TryGetValue(actionId, out var target))
            {
                return Failure("任务目标已失效，请刷新页面");
            }

            var sessionId = new MongoId(profileId);
            if (profileActivityService.IsRaidActive(sessionId))
            {
                return Failure("战局中或地图转移期间不能跳过任务");
            }

            // GetPmcProfile 只物化当前会话对应的存档，不遍历或加载其他玩家。
            var pmc = profileHelper.GetPmcProfile(sessionId);
            if (pmc is null)
            {
                return Failure("未找到玩家存档");
            }

            var questStatus = (pmc.Quests ?? []).FirstOrDefault(item => item.QId == target.QuestId);
            if (questStatus is null || questStatus.Status != QuestStatusEnum.Started)
            {
                return Failure("该任务当前不可跳过");
            }

            var resolved = ResolveQuest(pmc, target.QuestId);
            if (resolved is null)
            {
                return Failure("任务数据不存在，请刷新页面");
            }

            var topLevel = (resolved.Quest.Conditions.AvailableForFinish ?? [])
                .Where(condition => string.IsNullOrWhiteSpace(condition.ParentId))
                .ToList();
            var condition = topLevel.FirstOrDefault(item => item.Id == target.ConditionId);
            if (condition is null)
            {
                return Failure("该任务目标不可跳过");
            }

            questStatus.CompletedConditions ??= [];
            var conditionId = condition.Id.ToString();
            if (questStatus.CompletedConditions.Contains(conditionId, StringComparer.OrdinalIgnoreCase))
            {
                return Failure("该任务目标已经完成");
            }

            var ticketTpl = QuestSkipTicketService.TicketTpl.ToString();
            var ticketCount = stashService.CountTpl(pmc, ticketTpl, false);
            if (ticketCount < 1)
            {
                return Failure("主仓库中没有任务跳过券", ticketCount);
            }

            var snapshot = CaptureMutationSnapshot(pmc);
            try
            {
                var mutation = ApplyValidatedObjective(pmc, sessionId, questStatus, condition, topLevel);
                if (!mutation.Applied)
                {
                    throw new InvalidOperationException("扣除任务跳过券失败");
                }

                await profileSaveService.SaveAsync(sessionId);
                actions.Remove(actionId);
                var remaining = stashService.CountTpl(pmc, ticketTpl, false);
                logger.Info(
                    $"[SPT-BattlePass] 任务目标已跳过 profile={profileId} quest={target.QuestId} condition={target.ConditionId} remaining={remaining}"
                );
                return new QuestSkipResult
                {
                    Success = true,
                    Message = mutation.AvailableForFinish ? "目标已跳过，任务现在可以交付" : "任务目标已跳过",
                    TicketCount = remaining,
                    QuestAvailableForFinish = mutation.AvailableForFinish,
                };
            }
            catch (Exception ex)
            {
                RestoreMutationSnapshot(pmc, snapshot);
                try
                {
                    await profileSaveService.SaveAsync(sessionId);
                }
                catch (Exception rollbackSaveException)
                {
                    logger.Error($"[SPT-BattlePass] 任务跳过回滚落盘失败 profile={profileId}: {rollbackSaveException}");
                }

                logger.Error(
                    $"[SPT-BattlePass] 任务目标跳过失败并已回滚 profile={profileId} quest={target.QuestId} condition={target.ConditionId}: {ex}"
                );
                var remaining = stashService.CountTpl(pmc, ticketTpl, false);
                return Failure("保存失败，任务和跳过券均未发生变化", remaining);
            }
        }
        catch (Exception ex)
        {
            logger.Error($"[SPT-BattlePass] 任务目标跳过请求失败 profile={profileId}: {ex}");
            return Failure("跳过失败，请刷新页面后重试");
        }
        finally
        {
            gate.Release();
        }
    }

    private ResolvedQuest? ResolveQuest(PmcData pmc, MongoId questId)
    {
        if (databaseService.GetQuests().TryGetValue(questId, out var fixedQuest))
        {
            return new ResolvedQuest(fixedQuest, false, null);
        }

        foreach (var group in pmc.RepeatableQuests ?? [])
        {
            var repeatable = (group.ActiveQuests ?? []).FirstOrDefault(item => item.Id == questId);
            if (repeatable is not null)
            {
                var groupName = group.Name ?? repeatable.SptRepatableGroupName;
                return new ResolvedQuest(repeatable, true, groupName);
            }
        }

        return null;
    }

    /// <summary>捕获本次跳过可能修改的完整存档片段，保存失败时按片段原样恢复。</summary>
    internal QuestSkipMutationSnapshot CaptureMutationSnapshot(PmcData pmc) => new(
        cloner.Clone(pmc.Inventory),
        cloner.Clone(pmc.Quests),
        cloner.Clone(pmc.TaskConditionCounters)
    );

    internal static void RestoreMutationSnapshot(PmcData pmc, QuestSkipMutationSnapshot snapshot)
    {
        pmc.Inventory = snapshot.Inventory;
        pmc.Quests = snapshot.Quests;
        pmc.TaskConditionCounters = snapshot.TaskConditionCounters;
    }

    /// <summary>仅在外层全部校验完成后调用；原子地扣一张券并完成一个顶层目标。</summary>
    internal QuestSkipMutationResult ApplyValidatedObjective(
        PmcData pmc,
        MongoId sessionId,
        QuestStatus questStatus,
        QuestCondition condition,
        IReadOnlyCollection<QuestCondition> topLevelConditions
    )
    {
        if (stashService.RemoveTpl(pmc, sessionId, QuestSkipTicketService.TicketTpl.ToString(), 1, false) != 1)
        {
            return new QuestSkipMutationResult(false, false);
        }

        questStatus.CompletedConditions ??= [];
        var conditionId = condition.Id.ToString();
        questStatus.CompletedConditions.Add(conditionId);
        if (pmc.TaskConditionCounters?.TryGetValue(condition.Id, out var counter) == true)
        {
            counter.Value = Math.Max(counter.Value ?? 0, condition.Value ?? 1);
        }

        var completed = new HashSet<string>(questStatus.CompletedConditions, StringComparer.OrdinalIgnoreCase);
        var availableForFinish = topLevelConditions.Count > 0
            && topLevelConditions.All(item => completed.Contains(item.Id.ToString()));
        if (availableForFinish)
        {
            questStatus.Status = QuestStatusEnum.AvailableForFinish;
            questStatus.StatusTimers ??= [];
            questStatus.StatusTimers[QuestStatusEnum.AvailableForFinish] = timeUtil.GetTimeStamp();
        }

        return new QuestSkipMutationResult(true, availableForFinish);
    }

    private static QuestSkipResult Failure(string message, int ticketCount = 0) => new()
    {
        Success = false,
        Message = message,
        TicketCount = ticketCount,
    };

    private sealed record QuestSkipTarget(MongoId QuestId, MongoId ConditionId);
    private sealed record ResolvedQuest(Quest Quest, bool IsRepeatable, string? RepeatableGroupName);
}

internal sealed record QuestSkipMutationSnapshot(
    BotBaseInventory? Inventory,
    List<QuestStatus>? Quests,
    Dictionary<MongoId, TaskConditionCounter>? TaskConditionCounters
);

internal readonly record struct QuestSkipMutationResult(bool Applied, bool AvailableForFinish);
