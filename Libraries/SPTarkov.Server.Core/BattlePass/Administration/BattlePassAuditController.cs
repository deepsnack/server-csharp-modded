using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SPTarkov.DI.Annotations;

namespace SPTarkov.Server.Core.BattlePass.Administration;

/// <summary>协管操作审计、保留策略和管理员回溯 API。</summary>
[Injectable]
[ApiController]
[Route("battlepass/api/admin/audit")]
public sealed class BattlePassAuditController(
    BattlePassAdminSessionService sessionService,
    BattlePassReviewService reviewService,
    BattlePassChangeStore changeStore) : ControllerBase
{
    private BattlePassAdminPrincipal? AuthAdmin()
    {
        var token = Request.Headers["X-BP-Admin-Token"].FirstOrDefault()
            ?? Request.Headers["X-Admin-Token"].FirstOrDefault();
        var principal = sessionService.ValidateToken(token);
        return principal?.IsAdmin == true ? principal : null;
    }

    [HttpGet("")]
    public object Query([FromQuery] string? module = null, [FromQuery] string? eventType = null, [FromQuery] int limit = 100)
    {
        if (AuthAdmin() is null) return new { success = false, message = "未授权" };
        return new
        {
            success = true,
            retentionDays = BattlePassModConfig.GetAuditLogRetentionDays(),
            items = changeStore.QueryAudit(module, eventType, limit),
        };
    }

    [HttpGet("settings")]
    public object GetSettings()
    {
        if (AuthAdmin() is null) return new { success = false, message = "未授权" };
        return new { success = true, retentionDays = BattlePassModConfig.GetAuditLogRetentionDays() };
    }

    [HttpPatch("settings")]
    public object UpdateSettings([FromBody] JsonElement body)
    {
        if (AuthAdmin() is null) return new { success = false, message = "未授权" };
        if (!body.TryGetProperty("retentionDays", out var value) || !value.TryGetInt32(out var days) || days is < 1 or > 3650)
            return new { success = false, message = "retentionDays 必须是 1-3650 天" };

        var saved = BattlePassModConfig.SaveAuditLogRetentionDays(days);
        var removed = changeStore.PurgeExpiredAudit(saved);
        return new { success = true, retentionDays = saved, purged = removed };
    }

    [HttpPost("{auditId}/rollback")]
    public object Rollback(string auditId, [FromBody] JsonElement body)
    {
        var admin = AuthAdmin();
        if (admin is null) return new { success = false, message = "未授权" };
        var reason = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("reason", out var value)
            ? value.GetString()
            : null;
        var result = reviewService.RollbackAudit(admin, auditId, reason);
        return new
        {
            success = result.Ok,
            message = result.Message,
            auditId = result.AuditId,
            rollbackAuditId = result.RollbackAuditId,
            resultRevision = result.ResultRevision,
        };
    }
}
