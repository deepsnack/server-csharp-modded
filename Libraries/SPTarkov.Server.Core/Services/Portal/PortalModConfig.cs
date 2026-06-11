using System.Text;
using System.Text.Json;

namespace SPTarkov.Server.Core.Services.Portal;

/// <summary>
/// 从相邻 SptManagerPortal 的 portal.json 读取 Portal 自身监听端口，供 sidecar 向 Portal 自注册。
/// 按 cwd 定位（<c>&lt;SPT 根&gt;/user/mods/SptManagerPortal/assets/configs/portal.json</c>）。
/// Portal 未部署或字段缺失时返回 null，调用方回落默认端口 7790。
/// </summary>
internal static class PortalModConfig
{
    private static readonly JsonDocumentOptions DocOpts = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static int? TryReadPortalPort()
    {
        try
        {
            var portalConfigPath = Path.Combine(
                Directory.GetCurrentDirectory(), "user", "mods", "SptManagerPortal", "assets", "configs", "portal.json");
            if (!File.Exists(portalConfigPath)) return null;

            using var doc = JsonDocument.Parse(
                File.ReadAllText(portalConfigPath, Encoding.UTF8), DocOpts);

            if (doc.RootElement.TryGetProperty("webPanel", out var wp) &&
                wp.TryGetProperty("port", out var portEl) &&
                portEl.ValueKind == JsonValueKind.Number)
                return portEl.GetInt32();
        }
        catch { /* portal.json 不存在或解析失败，回落默认端口 */ }
        return null;
    }
}
