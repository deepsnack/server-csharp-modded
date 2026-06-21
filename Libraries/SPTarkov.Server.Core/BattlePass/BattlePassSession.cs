using System.Collections.Concurrent;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>
///     通行证网页会话：玩家用用户名+密码登录（经 PasswordStore 校验）后签发的 token → profileId 映射。
///     内存态，server 重启失效（前端 sessionStorage 也随之清空）。仿 WebRegisterController.AdminTokens。
/// </summary>
public static class BattlePassSession
{
    private static readonly ConcurrentDictionary<string, string> Tokens = new(); // token -> profileId

    public static string Issue(string profileId)
    {
        var token = Guid.NewGuid().ToString("N");
        Tokens[token] = profileId;
        return token;
    }

    /// <summary>解析 token，返回 profileId；无效返回 null。</summary>
    public static string? Resolve(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        return Tokens.TryGetValue(token, out var pid) ? pid : null;
    }
}
