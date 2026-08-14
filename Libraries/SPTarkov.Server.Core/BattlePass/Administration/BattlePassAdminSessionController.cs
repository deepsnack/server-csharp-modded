using Microsoft.AspNetCore.Mvc;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Services;

namespace SPTarkov.Server.Core.BattlePass.Administration;

/// <summary>管理会话交换与主体查询 API。</summary>
[Injectable]
[ApiController]
[Route("battlepass/api/admin")]
public class BattlePassAdminSessionController(
    BattlePassAdminSessionService sessionService,
    IAdminTokenService adminTokenService
) : ControllerBase
{
    /// <summary>交换管理会话（正常管理员用 X-Admin-Token，协管用 X-BP-Token）。</summary>
    [HttpPost("session/exchange")]
    public object Exchange()
    {
        // 优先检查正常管理员
        var adminToken = Request.Headers["X-Admin-Token"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(adminToken) && adminTokenService.IsAdminAuthorized(adminToken))
        {
            var bpToken = sessionService.ExchangeAdmin();
            return new { success = true, token = bpToken, actorType = "admin" };
        }

        // 协管：通过玩家会话 token 交换
        var bpPlayerToken = Request.Headers["X-BP-Token"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(bpPlayerToken))
        {
            var profileId = BattlePassSession.Resolve(bpPlayerToken);
            if (profileId is not null)
            {
                var displayName = profileId; // 简化：后续可从 profile 查昵称
                var token = sessionService.ExchangeCollaborator(profileId, displayName);
                if (token is not null)
                {
                    return new { success = true, token, actorType = "collaborator" };
                }

                return new { success = false, message = "未获得协管授权" };
            }
        }

        return new { success = false, message = "未授权" };
    }

    /// <summary>获取当前主体信息。</summary>
    [HttpGet("me")]
    public object GetMe()
    {
        var token = Request.Headers["X-BP-Admin-Token"].FirstOrDefault()
            ?? Request.Headers["X-Admin-Token"].FirstOrDefault();
        var principal = sessionService.ValidateToken(token);
        if (principal is null) return new { success = false, message = "未授权" };

        return new
        {
            success = true,
            actorType = principal.ActorType,
            actorId = principal.ActorId,
            displayName = principal.DisplayName,
            capabilities = principal.Capabilities.ToList(),
            issuedUtc = principal.IssuedUtc,
            expiresUtc = principal.ExpiresUtc,
        };
    }
}
