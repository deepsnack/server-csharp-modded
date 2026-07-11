using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace SPTarkov.Server.Core.BattlePass.Administration;

/// <summary>
///     审核服务：协管提交变更、管理员批准/驳回/编辑后批准。
///     <para>批准采用同步应用语义——批准接口成功返回前业务变更已经激活。</para>
/// </summary>
// 必须单例：模块处理器 _handlers 仅在启动时（BattlePassMod.OnLoad）注册一次，
// 若为默认 Scoped，则每个 HTTP 请求拿到的是 _handlers 为空的新实例，
// 协管提交任何模块都会误判为「不支持的模块」。与 BattlePassAdminSessionService 同理。
[Injectable(InjectionType.Singleton)]
public class BattlePassReviewService(
    BattlePassChangeStore changeStore,
    BattlePassReviewResultNotifier resultNotifier,
    ISptLogger<BattlePassReviewService> logger
)
{
    private static readonly object ApplyLock = new();
    private readonly Dictionary<string, IBattlePassChangeHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>注册模块处理器（DI 容器初始化后由 startup 调用）。</summary>
    public void RegisterHandler(IBattlePassChangeHandler handler)
    {
        _handlers[handler.Module] = handler;
    }

    /// <summary>获取处理器。</summary>
    public IBattlePassChangeHandler? GetHandler(string module)
    {
        return _handlers.GetValueOrDefault(module);
    }

    /// <summary>协管提交变更。返回 (成功, changeId/错误消息, 是否新建)。created=false 表示复用了同目标的既有待审变更。</summary>
    public (bool ok, string result, bool created) Submit(
        BattlePassAdminPrincipal principal,
        string module,
        string commandType,
        JsonElement input)
    {
        if (!_handlers.TryGetValue(module, out var handler))
        {
            return (false, "不支持的模块", false);
        }

        if (!handler.CommandTypes.Contains(commandType))
        {
            return (false, "不支持的命令类型", false);
        }

        if (!principal.HasCapability(handler.RequiredCapability))
        {
            return (false, "权限不足", false);
        }

        // 规范化与校验
        object normalized;
        try
        {
            normalized = handler.Normalize(commandType, input);
        }
        catch (Exception ex)
        {
            return (false, $"输入格式错误: {ex.Message}", false);
        }

        var error = handler.Validate(commandType, normalized);
        if (error is not null)
        {
            return (false, error, false);
        }

        var targetKey = handler.GetTargetKey(commandType, normalized);

        // 同一 targetKey 只允许一个 pending/applying
        var existing = changeStore.FindPendingByTargetKey(targetKey);
        if (existing is not null)
        {
            return (true, existing.Id, false); // 返回已有的 changeId（未新建，不重复通知）
        }

        var currentSnapshot = handler.GetCurrentSnapshot(targetKey);
        var baseRevision = handler.GetRevision(currentSnapshot);
        var operation = InferOperation(commandType, currentSnapshot);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var change = new BpChangeRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            Module = module,
            CommandType = commandType,
            Operation = operation,
            TargetType = module,
            TargetId = handler.GetTargetDisplayName(commandType, normalized),
            TargetDisplayName = handler.GetTargetDisplayName(commandType, normalized),
            TargetKey = targetKey,
            Summary = handler.Describe(commandType, normalized, currentSnapshot),
            Actor = new BpChangeActor
            {
                ActorId = principal.ActorId,
                DisplayName = principal.DisplayName,
                ProfileId = principal.IsCollaborator ? principal.ActorId : null,
            },
            CreatedUtc = now,
            UpdatedUtc = now,
            Version = 1,
            BaseRevision = baseRevision,
            BeforePayload = currentSnapshot,
            // 必须保存 handler 可再次解析的原始输入形状。删除 handler 普遍要求 { id } 对象，
            // 不能只保存规范化后的 string，更不能写成 null。
            ProposedPayload = input.Clone(),
            Status = "pending",
            History =
            [
                new BpChangeEvent
                {
                    Type = "submit",
                    Timestamp = now,
                    ActorId = principal.ActorId,
                    ActorDisplayName = principal.DisplayName,
                }
            ],
        };

        changeStore.SaveChange(change);
        SaveAudit("submit", change, new BpChangeActor
        {
            ActorId = principal.ActorId,
            DisplayName = principal.DisplayName,
            ProfileId = principal.IsCollaborator ? principal.ActorId : null,
        });
        if (principal.IsCollaborator)
        {
            resultNotifier.NotifySubmitted(change);
        }

        return (true, change.Id, true);
    }

    /// <summary>协管编辑本人尚未处理的提交；保留 change id，并重新计算目标与基线。</summary>
    public ReviewEditResult Edit(
        BattlePassAdminPrincipal editor,
        string changeId,
        JsonElement input,
        int expectedVersion)
    {
        lock (ApplyLock)
        {
            var change = changeStore.GetChange(changeId);
            if (change is null)
            {
                return new ReviewEditResult { Ok = false, Message = "变更请求不存在" };
            }

            if (!editor.IsCollaborator
                || !string.Equals(change.Actor.ActorId, editor.ActorId, StringComparison.OrdinalIgnoreCase))
            {
                return new ReviewEditResult { Ok = false, Message = "只能编辑自己的协管提交" };
            }

            if (change.Status != "pending")
            {
                return new ReviewEditResult { Ok = false, Message = $"当前状态 {change.Status} 不允许编辑" };
            }

            if (expectedVersion < 1 || change.Version != expectedVersion)
            {
                return new ReviewEditResult
                {
                    Ok = false,
                    Message = "审核内容已更新，请刷新后重试",
                    Version = change.Version,
                };
            }

            if (!_handlers.TryGetValue(change.Module, out var handler))
            {
                return new ReviewEditResult { Ok = false, Message = "模块处理器不可用" };
            }

            if (!editor.HasCapability(handler.RequiredCapability))
            {
                return new ReviewEditResult { Ok = false, Message = "权限不足" };
            }

            object normalized;
            try
            {
                normalized = handler.Normalize(change.CommandType, input);
            }
            catch (Exception ex)
            {
                return new ReviewEditResult { Ok = false, Message = $"输入格式错误: {ex.Message}" };
            }

            var validationError = handler.Validate(change.CommandType, normalized);
            if (validationError is not null)
            {
                return new ReviewEditResult { Ok = false, Message = validationError };
            }

            var targetKey = handler.GetTargetKey(change.CommandType, normalized);
            var duplicate = changeStore.FindPendingByTargetKey(targetKey);
            if (duplicate is not null && !string.Equals(duplicate.Id, change.Id, StringComparison.OrdinalIgnoreCase))
            {
                return new ReviewEditResult { Ok = false, Message = "该目标已有其它待审核变更" };
            }

            var currentSnapshot = handler.GetCurrentSnapshot(targetKey);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            change.TargetKey = targetKey;
            change.TargetId = handler.GetTargetDisplayName(change.CommandType, normalized);
            change.TargetDisplayName = change.TargetId;
            change.Summary = handler.Describe(change.CommandType, normalized, currentSnapshot);
            change.Operation = InferOperation(change.CommandType, currentSnapshot);
            change.BeforePayload = currentSnapshot;
            change.ProposedPayload = input.Clone();
            change.FinalPayload = null;
            change.BaseRevision = handler.GetRevision(currentSnapshot);
            change.UpdatedUtc = now;
            change.Version++;
            change.History.Add(new BpChangeEvent
            {
                Type = "edit",
                Timestamp = now,
                ActorId = editor.ActorId,
                ActorDisplayName = editor.DisplayName,
                Detail = "协管更新待审内容",
            });
            changeStore.SaveChange(change);

            return new ReviewEditResult
            {
                Ok = true,
                Message = "已更新待审核内容",
                ChangeId = change.Id,
                Version = change.Version,
                UpdatedUtc = change.UpdatedUtc,
            };
        }
    }

    /// <summary>管理员批准变更。同步执行 ApplyAndActivate，成功前不返回。</summary>
    public ReviewApplyResult Approve(
        BattlePassAdminPrincipal reviewer,
        string changeId,
        object? editedPayload = null,
        int? expectedVersion = null,
        bool sendNotification = true)
    {
        lock (ApplyLock)
        {
            var change = changeStore.GetChange(changeId);
            if (change is null)
            {
                return new ReviewApplyResult { Ok = false, Message = "变更请求不存在" };
            }

            if (expectedVersion is > 0 && change.Version != expectedVersion.Value)
            {
                return new ReviewApplyResult
                {
                    Ok = false,
                    Message = "审核内容已更新，请刷新详情后重试",
                    Status = change.Status,
                    Version = change.Version,
                };
            }

            if (change.Status is not "pending" and not "conflict" and not "failed")
            {
                return new ReviewApplyResult { Ok = false, Message = $"当前状态 {change.Status} 不允许批准" };
            }

            if (!_handlers.TryGetValue(change.Module, out var handler))
            {
                return new ReviewApplyResult { Ok = false, Message = "模块处理器不可用" };
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // 编辑后批准
            if (editedPayload is not null)
            {
                change.FinalPayload = editedPayload is JsonElement element ? element.Clone() : editedPayload;
            }

            var payload = change.FinalPayload ?? change.ProposedPayload;
            if (payload is null && !change.CommandType.Contains("delete", StringComparison.OrdinalIgnoreCase))
            {
                return new ReviewApplyResult { Ok = false, Message = "缺少变更内容" };
            }

            // 检查 baseRevision 冲突
            var currentSnapshot = handler.GetCurrentSnapshot(change.TargetKey);
            var currentRevision = handler.GetRevision(currentSnapshot);
            if (!string.Equals(currentRevision, change.BaseRevision, StringComparison.OrdinalIgnoreCase))
            {
                change.Status = "conflict";
                change.UpdatedUtc = now;
                change.Version++;
                change.History.Add(new BpChangeEvent
                {
                    Type = "conflict",
                    Timestamp = now,
                    ActorId = reviewer.ActorId,
                    ActorDisplayName = reviewer.DisplayName,
                    Detail = $"期望 {change.BaseRevision[..8]}，实际 {currentRevision[..Math.Min(8, currentRevision.Length)]}",
                });
                changeStore.SaveChange(change);
                return new ReviewApplyResult { Ok = false, Message = "基线版本已变化（冲突）", Status = "conflict" };
            }

            // 标记 applying
            change.Status = "applying";
            change.Reviewer = new BpChangeActor { ActorId = reviewer.ActorId, DisplayName = reviewer.DisplayName };
            change.ReviewedUtc = now;
            change.UpdatedUtc = now;
            change.Version++;
            changeStore.SaveChange(change);

            // 执行 ApplyAndActivate
            try
            {
                var payloadElement = payload is JsonElement je
                    ? je
                    : JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(payload));

                var normalized = handler.Normalize(change.CommandType, payloadElement);
                var validationError = handler.Validate(change.CommandType, normalized);
                if (validationError is not null)
                {
                    change.Status = "failed";
                    change.FailureMessage = validationError;
                    change.UpdatedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    change.Version++;
                    changeStore.SaveChange(change);
                    return new ReviewApplyResult { Ok = false, Message = validationError, Status = "failed" };
                }

                var resultRevision = handler.ApplyAndActivate(change.CommandType, normalized, change.BaseRevision, change.Id);

                // 确认生效
                change.Status = "applied";
                change.AppliedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                change.ResultRevision = resultRevision;
                change.UpdatedUtc = change.AppliedUtc;
                change.Version++;
                change.History.Add(new BpChangeEvent
                {
                    Type = "approve",
                    Timestamp = change.AppliedUtc,
                    ActorId = reviewer.ActorId,
                    ActorDisplayName = reviewer.DisplayName,
                    Detail = editedPayload is not null ? "编辑后批准" : "批准",
                });
                changeStore.SaveChange(change);

                SaveAudit(
                    "approve",
                    change,
                    new BpChangeActor { ActorId = reviewer.ActorId, DisplayName = reviewer.DisplayName },
                    beforePayload: change.BeforePayload,
                    afterPayload: handler.GetCurrentSnapshot(change.TargetKey),
                    resultRevision: resultRevision,
                    reversible: true,
                    detail: editedPayload is not null ? "编辑后批准" : "批准");
                var notificationQueued = sendNotification && resultNotifier.NotifyApproved(change);

                return new ReviewApplyResult
                {
                    Ok = true,
                    Message = "批准成功，变更已即时生效",
                    Status = "applied",
                    ChangeId = change.Id,
                    AppliedUtc = change.AppliedUtc,
                    ResultRevision = resultRevision,
                    Version = change.Version,
                    NotificationQueued = notificationQueued,
                };
            }
            catch (Exception ex)
            {
                logger.Error($"审核批准执行失败 [{change.Id}]: {ex.Message}");
                change.Status = "failed";
                change.FailureCode = ex.GetType().Name;
                change.FailureMessage = ex.Message;
                change.UpdatedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                change.Version++;
                change.History.Add(new BpChangeEvent
                {
                    Type = "apply_failed",
                    Timestamp = change.UpdatedUtc,
                    ActorId = reviewer.ActorId,
                    ActorDisplayName = reviewer.DisplayName,
                    Detail = ex.Message,
                });
                changeStore.SaveChange(change);
                return new ReviewApplyResult { Ok = false, Message = $"应用失败: {ex.Message}", Status = "failed" };
            }
        }
    }

    /// <summary>批量批准完成后，按协管聚合发送审核通过结果。</summary>
    public int NotifyApprovedBatch(IReadOnlyCollection<BpChangeRequest> changes)
    {
        return changes.Count == 0 ? 0 : resultNotifier.NotifyApprovedBatch(changes);
    }

    /// <summary>驳回变更请求。</summary>
    public (bool ok, string message) Reject(
        BattlePassAdminPrincipal reviewer,
        string changeId,
        string reason,
        int? expectedVersion = null)
    {
        lock (ApplyLock)
        {
            return RejectCore(reviewer, changeId, reason, expectedVersion);
        }
    }

    private (bool ok, string message) RejectCore(
        BattlePassAdminPrincipal reviewer,
        string changeId,
        string reason,
        int? expectedVersion)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
        {
            return (false, "驳回理由必填且不超过 500 字");
        }

        var change = changeStore.GetChange(changeId);
        if (change is null) return (false, "变更请求不存在");
        if (expectedVersion is > 0 && change.Version != expectedVersion.Value)
            return (false, "审核内容已更新，请刷新详情后重试");
        if (change.Status != "pending") return (false, $"当前状态 {change.Status} 不允许驳回");

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        change.Status = "rejected";
        change.Reviewer = new BpChangeActor { ActorId = reviewer.ActorId, DisplayName = reviewer.DisplayName };
        change.ReviewedUtc = now;
        change.ReviewReason = reason;
        change.UpdatedUtc = now;
        change.Version++;
        change.History.Add(new BpChangeEvent
        {
            Type = "reject",
            Timestamp = now,
            ActorId = reviewer.ActorId,
            ActorDisplayName = reviewer.DisplayName,
            Detail = reason,
        });
        changeStore.SaveChange(change);
        SaveAudit(
            "reject",
            change,
            new BpChangeActor { ActorId = reviewer.ActorId, DisplayName = reviewer.DisplayName },
            detail: reason);
        resultNotifier.NotifyRejected(change);
        return (true, "已驳回");
    }

    /// <summary>管理员根据批准审计记录撤销对应业务改动。</summary>
    public AuditRollbackResult RollbackAudit(BattlePassAdminPrincipal admin, string auditId, string? reason = null)
    {
        lock (ApplyLock)
        {
            var audit = changeStore.GetAudit(auditId);
            if (audit is null)
                return new AuditRollbackResult { Ok = false, Message = "审计日志不存在" };
            if (!string.Equals(audit.EventType, "approve", StringComparison.OrdinalIgnoreCase) || !audit.Reversible)
                return new AuditRollbackResult { Ok = false, Message = "该日志不包含可回溯的已批准改动" };
            if (audit.RolledBack)
                return new AuditRollbackResult { Ok = false, Message = "该改动已经回溯，不能重复执行" };
            if (audit.ResultRevision is null)
                return new AuditRollbackResult { Ok = false, Message = "审计日志缺少应用后版本，无法安全回溯" };
            if (!_handlers.TryGetValue(audit.Module, out var handler))
                return new AuditRollbackResult { Ok = false, Message = "模块处理器不可用" };

            try
            {
                var rollbackRevision = handler.RestoreSnapshot(
                    audit.TargetKey,
                    audit.BeforePayload,
                    audit.ResultRevision,
                    audit.Id);
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var actor = new BpChangeActor { ActorId = admin.ActorId, DisplayName = admin.DisplayName };

                audit.RolledBack = true;
                audit.RolledBackUtc = now;
                audit.RolledBackBy = actor;
                audit.RollbackResultRevision = rollbackRevision;
                changeStore.SaveAudit(audit);

                var rollbackAudit = new BpAuditLogEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    EventType = "rollback",
                    ChangeId = audit.ChangeId,
                    Module = audit.Module,
                    CommandType = audit.CommandType,
                    Operation = "rollback",
                    TargetKey = audit.TargetKey,
                    TargetDisplayName = audit.TargetDisplayName,
                    Summary = $"回溯：{audit.Summary}",
                    Actor = actor,
                    SubmittedBy = audit.SubmittedBy,
                    CreatedUtc = now,
                    Detail = string.IsNullOrWhiteSpace(reason) ? "管理员一键回溯" : reason.Trim(),
                    BeforePayload = audit.AfterPayload,
                    AfterPayload = handler.GetCurrentSnapshot(audit.TargetKey),
                    ResultRevision = rollbackRevision,
                    Reversible = false,
                    RollbackOfAuditId = audit.Id,
                };
                changeStore.SaveAudit(rollbackAudit);

                var change = changeStore.GetChange(audit.ChangeId);
                if (change is not null)
                {
                    change.Status = "rolled_back";
                    change.UpdatedUtc = now;
                    change.Version++;
                    change.History.Add(new BpChangeEvent
                    {
                        Type = "rollback",
                        Timestamp = now,
                        ActorId = admin.ActorId,
                        ActorDisplayName = admin.DisplayName,
                        Detail = rollbackAudit.Detail,
                    });
                    changeStore.SaveChange(change);
                }

                return new AuditRollbackResult
                {
                    Ok = true,
                    Message = "回溯成功，原改动已撤销",
                    AuditId = audit.Id,
                    RollbackAuditId = rollbackAudit.Id,
                    ResultRevision = rollbackRevision,
                };
            }
            catch (ChangeConflictException ex)
            {
                return new AuditRollbackResult { Ok = false, Message = $"回溯冲突：{ex.Message}" };
            }
            catch (Exception ex)
            {
                logger.Error($"审计回溯失败 [{audit.Id}]: {ex.Message}");
                return new AuditRollbackResult { Ok = false, Message = $"回溯失败：{ex.Message}" };
            }
        }
    }

    public int PurgeExpiredAudit() => changeStore.PurgeExpiredAudit(BattlePassModConfig.GetAuditLogRetentionDays());

    private void SaveAudit(
        string eventType,
        BpChangeRequest change,
        BpChangeActor actor,
        object? beforePayload = null,
        object? afterPayload = null,
        string? resultRevision = null,
        bool reversible = false,
        string? detail = null)
    {
        changeStore.SaveAudit(new BpAuditLogEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            EventType = eventType,
            ChangeId = change.Id,
            Module = change.Module,
            CommandType = change.CommandType,
            Operation = change.Operation,
            TargetKey = change.TargetKey,
            TargetDisplayName = change.TargetDisplayName,
            Summary = change.Summary,
            Actor = actor,
            SubmittedBy = change.Actor,
            CreatedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Detail = detail,
            BeforePayload = beforePayload,
            AfterPayload = afterPayload,
            ResultRevision = resultRevision,
            Reversible = reversible,
        });
    }

    /// <summary>协管撤回。</summary>
    public (bool ok, string message) Withdraw(string actorId, string changeId, int? expectedVersion = null)
    {
        lock (ApplyLock)
        {
            return WithdrawCore(actorId, changeId, expectedVersion);
        }
    }

    private (bool ok, string message) WithdrawCore(string actorId, string changeId, int? expectedVersion)
    {
        var change = changeStore.GetChange(changeId);
        if (change is null) return (false, "变更请求不存在");
        if (expectedVersion is > 0 && change.Version != expectedVersion.Value)
            return (false, "审核内容已更新，请刷新后重试");
        if (change.Status != "pending") return (false, "只有 pending 状态才能撤回");
        if (!string.Equals(change.Actor.ActorId, actorId, StringComparison.OrdinalIgnoreCase))
        {
            return (false, "只能撤回自己的提交");
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        change.Status = "withdrawn";
        change.UpdatedUtc = now;
        change.Version++;
        change.History.Add(new BpChangeEvent
        {
            Type = "withdraw",
            Timestamp = now,
            ActorId = actorId,
            ActorDisplayName = change.Actor.DisplayName,
        });
        changeStore.SaveChange(change);
        return (true, "已撤回");
    }

    /// <summary>启动时崩溃恢复：扫描 applying 状态并尝试修复。</summary>
    public void RecoverOnStartup()
    {
        var applyingList = changeStore.FindApplying();
        foreach (var change in applyingList)
        {
            if (!_handlers.TryGetValue(change.Module, out var handler))
            {
                change.Status = "failed";
                change.FailureMessage = "模块处理器不可用";
                changeStore.SaveChange(change);
                continue;
            }

            var currentSnapshot = handler.GetCurrentSnapshot(change.TargetKey);
            var currentRevision = handler.GetRevision(currentSnapshot);

            if (change.ResultRevision is not null && string.Equals(currentRevision, change.ResultRevision, StringComparison.OrdinalIgnoreCase))
            {
                // 已经应用成功
                change.Status = "applied";
                change.AppliedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                changeStore.SaveChange(change);
                logger.Info($"崩溃恢复：{change.Id} 已确认 applied");
            }
            else if (string.Equals(currentRevision, change.BaseRevision, StringComparison.OrdinalIgnoreCase))
            {
                // 未应用，安全重试一次 — 标记回 pending 等管理员处理
                change.Status = "pending";
                change.History.Add(new BpChangeEvent
                {
                    Type = "recover",
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ActorId = "system",
                    ActorDisplayName = "系统恢复",
                    Detail = "启动恢复：未应用，标记为 pending",
                });
                changeStore.SaveChange(change);
                logger.Info($"崩溃恢复：{change.Id} 重置为 pending");
            }
            else
            {
                // 两者均不等，冲突
                change.Status = "conflict";
                change.History.Add(new BpChangeEvent
                {
                    Type = "recover",
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ActorId = "system",
                    ActorDisplayName = "系统恢复",
                    Detail = "启动恢复：revision 不匹配，标记为 conflict",
                });
                changeStore.SaveChange(change);
                logger.Warning($"崩溃恢复：{change.Id} 标记为 conflict");
            }
        }
    }

    private static string InferOperation(string commandType, object? currentState)
    {
        if (commandType.Contains("delete", StringComparison.OrdinalIgnoreCase)) return "delete";
        return currentState is null ? "create" : "update";
    }
}

/// <summary>审核批准结果。</summary>
public class ReviewApplyResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public string? Status { get; set; }
    public string? ChangeId { get; set; }
    public long AppliedUtc { get; set; }
    public string? ResultRevision { get; set; }
    public int Version { get; set; }
    public bool NotificationQueued { get; set; }
}

/// <summary>管理员审计回溯结果。</summary>
public class AuditRollbackResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public string? AuditId { get; set; }
    public string? RollbackAuditId { get; set; }
    public string? ResultRevision { get; set; }
}

/// <summary>协管编辑待审内容结果。</summary>
public class ReviewEditResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public string? ChangeId { get; set; }
    public int Version { get; set; }
    public long UpdatedUtc { get; set; }
}
