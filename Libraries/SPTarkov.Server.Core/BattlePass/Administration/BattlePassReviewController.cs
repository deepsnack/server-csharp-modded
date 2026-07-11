using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.BattlePass.ItemControl;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Models.Common;

namespace SPTarkov.Server.Core.BattlePass.Administration;

/// <summary>审核 API 控制器。</summary>
[Injectable]
[ApiController]
[Route("battlepass/api/admin/reviews")]
public class BattlePassReviewController(
    BattlePassAdminSessionService sessionService,
    BattlePassReviewService reviewService,
    BattlePassChangeStore changeStore,
    ItemSearchService itemSearch
) : ControllerBase
{
    private BattlePassAdminPrincipal? Auth()
    {
        var token = Request.Headers["X-BP-Admin-Token"].FirstOrDefault()
            ?? Request.Headers["X-Admin-Token"].FirstOrDefault();
        return sessionService.ValidateToken(token);
    }

    /// <summary>审核摘要：各模块待审数量。</summary>
    [HttpGet("summary")]
    public object GetSummary()
    {
        var principal = Auth();
        if (principal is null || !principal.IsAdmin)
            return new { success = false, message = "未授权" };

        var pending = changeStore.QueryChanges(status: "pending", limit: 1000);
        var byModule = pending.GroupBy(c => c.Module, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        return new { success = true, total = pending.Count, byModule };
    }

    /// <summary>查询审核列表。</summary>
    [HttpGet("")]
    public object Query(
        [FromQuery] string? module = null,
        [FromQuery] string? status = null,
        [FromQuery] string? actor = null,
        [FromQuery] int limit = 50)
    {
        var principal = Auth();
        if (principal is null) return new { success = false, message = "未授权" };

        // 协管只能看自己的提交
        var queryActor = principal.IsCollaborator ? principal.ActorId : actor;
        var items = changeStore.QueryChanges(module, status, queryActor, Math.Clamp(limit, 1, 200));
        return new { success = true, items };
    }

    /// <summary>
    ///     提交一条变更进入审核队列。协管与管理员均可提交（管理员通常直接在各模块页即时写入，
    ///     此端点主要供协管在专属页发起变更）。body：{ module, commandType, input:{…} }。
    /// </summary>
    [HttpPost("submit")]
    public object Submit([FromBody] JsonElement body)
    {
        var principal = Auth();
        if (principal is null) return new { success = false, message = "未授权" };

        var module = body.TryGetProperty("module", out var m) ? m.GetString() : null;
        var commandType = body.TryGetProperty("commandType", out var c) ? c.GetString() : null;
        if (string.IsNullOrWhiteSpace(module) || string.IsNullOrWhiteSpace(commandType))
            return new { success = false, message = "module 与 commandType 必填" };

        if (!body.TryGetProperty("input", out var input) || input.ValueKind == JsonValueKind.Undefined)
            return new { success = false, message = "缺少 input" };

        var (ok, result, created) = reviewService.Submit(principal, module, commandType, input);
        return ok
            ? new { success = true, changeId = result }
            : new { success = false, message = result };
    }

    /// <summary>查看单条变更详情。</summary>
    [HttpGet("{id}")]
    public object GetDetail(string id)
    {
        var principal = Auth();
        if (principal is null) return new { success = false, message = "未授权" };

        var change = changeStore.GetChange(id);
        if (change is null) return new { success = false, message = "不存在" };

        // 协管只能看自己的
        if (principal.IsCollaborator && !string.Equals(change.Actor.ActorId, principal.ActorId, StringComparison.OrdinalIgnoreCase))
            return new { success = false, message = "无权查看" };

        // 解析 payload 中引用的物品 tpl → 中文名，供审核页显示物品名而非裸 MongoId。
        // 批量解析：三张 locale 表一次性物化后复用（勿在循环里单发 ResolveItemNameZh，
        // 否则每个 tpl 都触发整表反序列化，既慢又拉长与其它请求并发读 DB 的时间窗）。
        // 仅收录能解析出非空名称者；解析失败/无效的 tpl 不入表，前端回退「未知物品」。
        var validTpls = BattlePassChangeDigest.CollectItemTpls(change)
            .Where(MongoId.IsValidMongoId)
            .Select(t => new MongoId(t));
        var itemNames = itemSearch.ResolveItemNamesZh(validTpls);

        return new { success = true, change, itemNames };
    }

    /// <summary>协管编辑本人尚未处理的提交。body：{ input:{…}, expectedVersion:n }。</summary>
    [HttpPost("{id}/edit")]
    public object Edit(string id, [FromBody] JsonElement body)
    {
        var principal = Auth();
        if (principal is null || !principal.IsCollaborator)
            return new { success = false, message = "未授权" };

        if (!body.TryGetProperty("input", out var input) || input.ValueKind == JsonValueKind.Undefined)
            return new { success = false, message = "缺少 input" };

        var expectedVersion = body.TryGetProperty("expectedVersion", out var versionProp)
            && versionProp.TryGetInt32(out var parsedVersion)
                ? parsedVersion
                : 0;
        if (expectedVersion < 1)
            return new { success = false, message = "expectedVersion 必填" };

        var result = reviewService.Edit(principal, id, input, expectedVersion);
        return result.Ok
            ? new
            {
                success = true,
                message = result.Message,
                changeId = result.ChangeId,
                version = result.Version,
                updatedUtc = result.UpdatedUtc,
            }
            : new
            {
                success = false,
                message = result.Message,
                changeId = (string?) null,
                version = result.Version,
                updatedUtc = 0L,
            };
    }

    /// <summary>批准。</summary>
    [HttpPost("{id}/approve")]
    public object Approve(string id, [FromQuery] int? expectedVersion = null)
    {
        var principal = Auth();
        if (principal is null || !principal.IsAdmin) return new { success = false, message = "未授权" };

        var result = reviewService.Approve(principal, id, expectedVersion: expectedVersion);
        if (!result.Ok)
            return new { success = false, message = result.Message, status = result.Status, version = result.Version };

        return new
        {
            success = true,
            outcome = "applied",
            changeId = result.ChangeId,
            module = changeStore.GetChange(id)?.Module,
            appliedUtc = result.AppliedUtc,
            resultRevision = result.ResultRevision,
            version = result.Version,
            notificationStatus = result.NotificationQueued ? "queued" : "skipped",
        };
    }

    /// <summary>编辑后批准。</summary>
    [HttpPost("{id}/approve-edited")]
    public object ApproveEdited(string id, [FromBody] JsonElement body)
    {
        var principal = Auth();
        if (principal is null || !principal.IsAdmin) return new { success = false, message = "未授权" };

        var result = reviewService.Approve(principal, id, editedPayload: body);
        if (!result.Ok)
            return new { success = false, message = result.Message, status = result.Status };

        return new
        {
            success = true,
            outcome = "applied",
            changeId = result.ChangeId,
            appliedUtc = result.AppliedUtc,
            resultRevision = result.ResultRevision,
            notificationStatus = result.NotificationQueued ? "queued" : "skipped",
        };
    }

    /// <summary>驳回。</summary>
    [HttpPost("{id}/reject")]
    public object Reject(string id, [FromBody] JsonElement body)
    {
        var principal = Auth();
        if (principal is null || !principal.IsAdmin) return new { success = false, message = "未授权" };

        var reason = body.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
        var expectedVersion = body.TryGetProperty("expectedVersion", out var versionProp)
            && versionProp.TryGetInt32(out var parsedVersion)
                ? parsedVersion
                : (int?) null;
        var (ok, message) = reviewService.Reject(principal, id, reason, expectedVersion);
        return new { success = ok, message };
    }

    /// <summary>撤回（协管自己）。</summary>
    [HttpPost("{id}/withdraw")]
    public object Withdraw(string id, [FromQuery] int? expectedVersion = null)
    {
        var principal = Auth();
        if (principal is null) return new { success = false, message = "未授权" };

        var (ok, message) = reviewService.Withdraw(principal.ActorId, id, expectedVersion);
        return new { success = ok, message };
    }

    /// <summary>批量批准。</summary>
    [HttpPost("batch/approve")]
    public object BatchApprove([FromBody] JsonElement body)
    {
        var principal = Auth();
        if (principal is null || !principal.IsAdmin) return new { success = false, message = "未授权" };

        var module = body.TryGetProperty("module", out var m) ? m.GetString() : null;
        var items = ParseBatchItems(body);

        if (items.Count == 0)
            return new { success = false, message = "items 或 ids 必填" };

        var results = new List<object>();
        var approvedChanges = new List<BpChangeRequest>();
        foreach (var item in items)
        {
            var change = changeStore.GetChange(item.Id);
            if (change is null || (!string.IsNullOrWhiteSpace(module)
                && !string.Equals(change.Module, module, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(new { id = item.Id, outcome = "skipped", message = "不存在或模块不匹配" });
                continue;
            }

            var result = reviewService.Approve(principal, item.Id, expectedVersion: item.ExpectedVersion, sendNotification: false);
            if (result.Ok)
            {
                var approved = changeStore.GetChange(item.Id);
                if (approved is not null)
                {
                    approvedChanges.Add(approved);
                }
            }

            results.Add(new { id = item.Id, outcome = result.Ok ? "applied" : result.Status ?? "failed", message = result.Message });
        }

        var notificationsQueued = reviewService.NotifyApprovedBatch(approvedChanges);
        return new
        {
            success = true,
            results,
            notificationStatus = notificationsQueued > 0 ? "queued" : "skipped",
            notificationsQueued,
        };
    }

    /// <summary>批量驳回。</summary>
    [HttpPost("batch/reject")]
    public object BatchReject([FromBody] JsonElement body)
    {
        var principal = Auth();
        if (principal is null || !principal.IsAdmin) return new { success = false, message = "未授权" };

        var module = body.TryGetProperty("module", out var m) ? m.GetString() : null;
        var items = ParseBatchItems(body);
        var reason = body.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";

        if (items.Count == 0 || string.IsNullOrWhiteSpace(reason))
            return new { success = false, message = "items/ids 和 reason 必填" };

        var results = new List<object>();
        foreach (var item in items)
        {
            var change = changeStore.GetChange(item.Id);
            if (change is null || (!string.IsNullOrWhiteSpace(module)
                && !string.Equals(change.Module, module, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(new { id = item.Id, outcome = "skipped", message = "不存在或模块不匹配" });
                continue;
            }

            var (ok, msg) = reviewService.Reject(principal, item.Id, reason, item.ExpectedVersion);
            results.Add(new { id = item.Id, outcome = ok ? "rejected" : "failed", message = msg });
        }

        return new { success = true, results };
    }

    private static List<BatchReviewItem> ParseBatchItems(JsonElement body)
    {
        var result = new List<BatchReviewItem>();
        if (body.TryGetProperty("items", out var itemsProp) && itemsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemsProp.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("id", out var idProp)
                    || string.IsNullOrWhiteSpace(idProp.GetString()))
                {
                    continue;
                }

                var expectedVersion = item.TryGetProperty("expectedVersion", out var versionProp)
                    && versionProp.TryGetInt32(out var parsedVersion)
                        ? parsedVersion
                        : (int?) null;
                result.Add(new BatchReviewItem(idProp.GetString()!, expectedVersion));
            }
        }
        else if (body.TryGetProperty("ids", out var idsProp) && idsProp.ValueKind == JsonValueKind.Array)
        {
            result.AddRange(idsProp.EnumerateArray()
                .Select(e => e.GetString() ?? "")
                .Where(id => id.Length > 0)
                .Select(id => new BatchReviewItem(id, null)));
        }

        return result.GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
    }

    private sealed record BatchReviewItem(string Id, int? ExpectedVersion);
}
