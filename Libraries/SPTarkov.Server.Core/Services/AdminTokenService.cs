using System.Collections.Concurrent;
using SPTarkov.DI.Annotations;

namespace SPTarkov.Server.Core.Services;

/// <summary>
///     WebRegister 管理员登录态域：签发/校验 admin token。
///     原为 WebRegisterController 的静态成员（<c>AdminTokens</c> / <c>IssueAdminToken</c> / <c>IsAdminAuthorized</c>），
///     被 BattlePass/Portal sidecar 跨模块静态调用——重构为 DI 注入接口，消除控制器静态全局状态与跨模块隐式耦合。
///     token 语义不变：server 重启即失效，admin 页用 <c>X-Admin-Token</c> 携带。
/// </summary>
public interface IAdminTokenService
{
    /// <summary>签发并登记一个管理员 token，返回给调用方。</summary>
    string IssueAdminToken();

    /// <summary>校验 token 是否有效（非空且已登记）。</summary>
    bool IsAdminAuthorized(string? token);
}

[Injectable(InjectionType.Singleton)]
public sealed class AdminTokenService : IAdminTokenService
{
    // 管理员 token（server 重启即失效；浏览器关闭后 sessionStorage 清空，客户端不会再发送旧 token）
    private readonly ConcurrentDictionary<string, byte> _adminTokens = new();

    public string IssueAdminToken()
    {
        var token = Guid.NewGuid().ToString("N");
        _adminTokens.TryAdd(token, 0);
        return token;
    }

    public bool IsAdminAuthorized(string? token)
    {
        return !string.IsNullOrEmpty(token) && _adminTokens.ContainsKey(token);
    }
}
