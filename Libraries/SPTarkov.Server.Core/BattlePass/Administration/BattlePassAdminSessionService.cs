using System.Security.Cryptography;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;

namespace SPTarkov.Server.Core.BattlePass.Administration;

/// <summary>
///     BattlePass 管理会话服务：正常管理员或协管交换管理会话 token。
///     <para>会话 token 高熵随机；空闲 30 分钟失效、绝对 8 小时失效；服务器重启后失效。</para>
/// </summary>
[Injectable(InjectionType.Singleton)]
public class BattlePassAdminSessionService(
    BattlePassChangeStore changeStore,
    IAdminTokenService adminTokenService,
    ISptLogger<BattlePassAdminSessionService> logger
)
{
    private const int IdleTimeoutSeconds = 30 * 60;       // 30 分钟
    private const int AbsoluteTimeoutSeconds = 8 * 3600;  // 8 小时
    private const int TokenByteLength = 32;

    private readonly Dictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>正常管理员通过 admin token 交换 BattlePass 管理会话。</summary>
    public string ExchangeAdmin()
    {
        var principal = new BattlePassAdminPrincipal
        {
            ActorType = "admin",
            ActorId = "admin",
            DisplayName = "管理员",
            Capabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "*" },
        };
        return CreateSession(principal);
    }

    /// <summary>协管通过 profileId 交换管理会话（需预检授权有效）。</summary>
    public string? ExchangeCollaborator(string profileId, string displayName)
    {
        profileId = BattlePassCollaboratorGrantPolicy.NormalizeProfileId(profileId);
        var grant = BattlePassCollaboratorGrantPolicy.Find(changeStore.GetGrants(), profileId, requireEnabled: true);

        if (grant is null)
        {
            return null;
        }

        var principal = new BattlePassAdminPrincipal
        {
            ActorType = "collaborator",
            ActorId = profileId,
            DisplayName = string.IsNullOrWhiteSpace(grant.UsernameSnapshot) ? displayName : grant.UsernameSnapshot,
            Capabilities = BattlePassCollaboratorGrantPolicy.NormalizeCapabilitySet(grant.Capabilities),
        };
        return CreateSession(principal);
    }

    /// <summary>验证并获取管理主体。每次调用都重新检查协管授权状态。</summary>
    /// <remarks>
    ///     兼容两类 token：优先按 BattlePass 会话 token 校验；若不是会话 token，
    ///     再回退到 WebRegister 原始 admin token（主后台 index.html 直接携带原始 token、
    ///     不走 session/exchange，管理员从主后台跳转到审核/协管页时即为此情形）。
    /// </remarks>
    public BattlePassAdminPrincipal? ValidateToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        lock (_lock)
        {
            if (!_sessions.TryGetValue(token, out var entry))
            {
                // 回退：WebRegister 原始 admin token 视为完整管理员主体
                if (adminTokenService.IsAdminAuthorized(token))
                {
                    var now2 = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    return new BattlePassAdminPrincipal
                    {
                        ActorType = "admin",
                        ActorId = "admin",
                        DisplayName = "管理员",
                        Capabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "*" },
                        IssuedUtc = now2,
                        ExpiresUtc = now2 + AbsoluteTimeoutSeconds,
                    };
                }

                return null;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (now > entry.Principal.ExpiresUtc || now - entry.LastAccessUtc > IdleTimeoutSeconds)
            {
                _sessions.Remove(token);
                return null;
            }

            // 协管：每次重新检查授权是否仍启用
            if (entry.Principal.IsCollaborator)
            {
                var grant = BattlePassCollaboratorGrantPolicy.Find(
                    changeStore.GetGrants(), entry.Principal.ActorId, requireEnabled: true);
                if (grant is null)
                {
                    _sessions.Remove(token);
                    return null;
                }

                // 更新能力（可能被管理员修改）
                entry.Principal.Capabilities = BattlePassCollaboratorGrantPolicy.NormalizeCapabilitySet(grant.Capabilities);
            }

            entry.LastAccessUtc = now;
            return entry.Principal;
        }
    }

    /// <summary>撤销指定 profileId 的所有会话（撤权时调用）。</summary>
    public void RevokeAllSessions(string profileId)
    {
        lock (_lock)
        {
            var toRemove = _sessions
                .Where(kv => string.Equals(kv.Value.Principal.ActorId, profileId, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in toRemove)
            {
                _sessions.Remove(key);
            }
        }
    }

    private string CreateSession(BattlePassAdminPrincipal principal)
    {
        var token = GenerateToken();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        principal.SessionId = Guid.NewGuid().ToString("N");
        principal.IssuedUtc = now;
        principal.ExpiresUtc = now + AbsoluteTimeoutSeconds;

        lock (_lock)
        {
            _sessions[token] = new SessionEntry { Principal = principal, LastAccessUtc = now };
        }

        return token;
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenByteLength);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    private sealed class SessionEntry
    {
        public BattlePassAdminPrincipal Principal { get; init; } = new();
        public long LastAccessUtc { get; set; }
    }
}
