using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Helpers;

namespace SPTarkov.Server.Core.BattlePass.Administration;

/// <summary>协管授权管理 API（仅正常管理员）。</summary>
[Injectable]
[ApiController]
[Route("battlepass/api/admin/access")]
public class BattlePassAccessController(
    BattlePassAdminSessionService sessionService,
    BattlePassChangeStore changeStore,
    ProfileHelper profileHelper,
    BattlePassService battlePassService
) : ControllerBase
{
    private BattlePassAdminPrincipal? AuthAdmin()
    {
        var token = Request.Headers["X-BP-Admin-Token"].FirstOrDefault()
            ?? Request.Headers["X-Admin-Token"].FirstOrDefault();
        var principal = sessionService.ValidateToken(token);
        return principal?.IsAdmin == true ? principal : null;
    }

    /// <summary>获取所有协管授权。</summary>
    [HttpGet("grants")]
    public object GetGrants()
    {
        if (AuthAdmin() is null) return new { success = false, message = "未授权" };
        var grants = changeStore.GetGrants();
        foreach (var grant in grants)
        {
            grant.Capabilities = BattlePassCollaboratorGrantPolicy.NormalizeCapabilities(grant.Capabilities);
        }

        return new { success = true, grants };
    }

    /// <summary>搜索可授权的真实玩家（排除 headless 与空存档），支持按账号名 / 游戏昵称模糊过滤。</summary>
    [HttpGet("players")]
    public object SearchPlayers([FromQuery] string? q = null)
    {
        if (AuthAdmin() is null) return new { success = false, message = "未授权" };

        var grantedIds = changeStore.GetGrants()
            .Select(g => BattlePassCollaboratorGrantPolicy.NormalizeProfileId(g.ProfileId))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 复用现有真实玩家搜索：结果为匿名对象 { profileId, username, nickname }。
        var players = battlePassService.SearchRealPlayers(q)
            .Select(p =>
            {
                var el = JsonSerializer.SerializeToElement(p);
                var pid = el.TryGetProperty("profileId", out var idEl) ? idEl.GetString() ?? "" : "";
                return new
                {
                    profileId = pid,
                    username = el.TryGetProperty("username", out var uEl) ? uEl.GetString() : null,
                    nickname = el.TryGetProperty("nickname", out var nEl) ? nEl.GetString() : null,
                    granted = grantedIds.Contains(pid),
                };
            })
            .ToList();

        return new { success = true, players };
    }

    /// <summary>授权新协管。</summary>
    [HttpPost("grants")]
    public object CreateGrant([FromBody] JsonElement body)
    {
        var admin = AuthAdmin();
        if (admin is null) return new { success = false, message = "未授权" };

        var profileId = BattlePassCollaboratorGrantPolicy.NormalizeProfileId(
            body.TryGetProperty("profileId", out var p) ? p.GetString() : null);
        if (string.IsNullOrWhiteSpace(profileId))
            return new { success = false, message = "profileId 必填" };

        // 排除 headless
        var pmc = profileHelper.GetPmcProfile(new Models.Common.MongoId(profileId));
        if (pmc is null)
            return new { success = false, message = "档案不存在" };

        var grants = changeStore.GetGrants();
        if (BattlePassCollaboratorGrantPolicy.Find(grants, profileId) is not null)
            return new { success = false, message = "已存在授权记录" };

        var caps = BattlePassCollaboratorGrantPolicy.DefaultCapabilities();
        if (body.TryGetProperty("capabilities", out var capsProp) && capsProp.ValueKind == JsonValueKind.Array)
        {
            caps = BattlePassCollaboratorGrantPolicy.NormalizeCapabilities(capsProp.EnumerateArray()
                .Select(e => e.GetString() ?? "")
                .Where(s => s.Length > 0));
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var grant = new BpCollaboratorGrant
        {
            ProfileId = profileId,
            UsernameSnapshot = pmc.Info?.Nickname ?? profileId,
            Enabled = true,
            Capabilities = caps,
            GrantedBy = admin.ActorId,
            GrantedUtc = now,
            UpdatedUtc = now,
        };
        grants.Add(grant);
        changeStore.SaveGrants(grants);
        return new { success = true, grant };
    }

    /// <summary>修改协管能力或启用/停用。</summary>
    [HttpPatch("grants/{profileId}")]
    public object UpdateGrant(string profileId, [FromBody] JsonElement body)
    {
        var admin = AuthAdmin();
        if (admin is null) return new { success = false, message = "未授权" };

        var grants = changeStore.GetGrants();
        profileId = BattlePassCollaboratorGrantPolicy.NormalizeProfileId(profileId);
        var grant = BattlePassCollaboratorGrantPolicy.Find(grants, profileId);
        if (grant is null) return new { success = false, message = "授权记录不存在" };

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (body.TryGetProperty("enabled", out var en))
            grant.Enabled = en.GetBoolean();

        if (body.TryGetProperty("capabilities", out var capsProp) && capsProp.ValueKind == JsonValueKind.Array)
        {
            grant.Capabilities = BattlePassCollaboratorGrantPolicy.NormalizeCapabilities(capsProp.EnumerateArray()
                .Select(e => e.GetString() ?? "")
                .Where(s => s.Length > 0));
        }

        grant.UpdatedBy = admin.ActorId;
        grant.UpdatedUtc = now;
        grant.Version++;

        changeStore.SaveGrants(grants);

        if (!grant.Enabled)
        {
            sessionService.RevokeAllSessions(profileId);
        }

        return new { success = true, grant };
    }

    /// <summary>永久删除协管授权记录。撤销/恢复权限使用 PATCH enabled。</summary>
    [HttpDelete("grants/{profileId}")]
    public object DeleteGrant(string profileId)
    {
        var admin = AuthAdmin();
        if (admin is null) return new { success = false, message = "未授权" };

        profileId = BattlePassCollaboratorGrantPolicy.NormalizeProfileId(profileId);
        var grants = changeStore.GetGrants();
        var removed = BattlePassCollaboratorGrantPolicy.RemoveAll(grants, profileId);
        if (removed == 0) return new { success = false, message = "授权记录不存在" };

        changeStore.SaveGrants(grants);
        sessionService.RevokeAllSessions(profileId);
        return new { success = true, deleted = removed };
    }
}
