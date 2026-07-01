using System.Text.Json;
using System.Text.Json.Serialization;

namespace SPTarkov.Server.Core.Services.Portal;

/// <summary>
/// 读 user/spt-manager-shared.json 的共享密钥（SSO tokenSecret + 管理员 keyHash），与 SptManagerPortal
/// 及其他子工具共用。WebRegister 跑在主服务器进程内，工作目录即 SPT 根，故按 cwd 定位共享文件
/// （<c>&lt;SPT 根&gt;/user/spt-manager-shared.json</c>），无需 ModHelper。
/// 本桥接只用到 tokenSecret（验证 Portal 短 token）；admin 登录仍由 WebRegister 自身的 adminPassword 负责。
/// </summary>
public static class PortalSharedKey
{
    private static string SharedFilePath =>
        Path.Combine(Directory.GetCurrentDirectory(), "user", "spt-manager-shared.json");

    public static string? TryGetTokenSecret()
    {
        try
        {
            var path = SharedFilePath;
            if (!File.Exists(path)) return null;
            var dto = JsonSerializer.Deserialize<SharedSecretsDto>(File.ReadAllText(path));
            return string.IsNullOrEmpty(dto?.TokenSecret) ? null : dto.TokenSecret;
        }
        catch
        {
            return null;
        }
    }

    public static string? TryGetPlayerTokenSecret()
    {
        try
        {
            var path = SharedFilePath;
            if (!File.Exists(path)) return null;
            var dto = JsonSerializer.Deserialize<SharedSecretsDto>(File.ReadAllText(path));
            return string.IsNullOrEmpty(dto?.PlayerTokenSecret) ? null : dto.PlayerTokenSecret;
        }
        catch { return null; }
    }

    private record SharedSecretsDto
    {
        [JsonPropertyName("keyHash")] public KeyHashDto? KeyHash { get; set; }
        [JsonPropertyName("tokenSecret")] public string TokenSecret { get; set; } = "";
        [JsonPropertyName("playerTokenSecret")] public string PlayerTokenSecret { get; set; } = "";
    }

    private record KeyHashDto
    {
        [JsonPropertyName("salt")] public string Salt { get; set; } = "";
        [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    }
}
