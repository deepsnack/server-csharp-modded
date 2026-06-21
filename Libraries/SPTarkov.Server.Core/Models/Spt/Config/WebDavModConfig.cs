using System.Text.Json;
using System.Text.Json.Serialization;

namespace SPTarkov.Server.Core.Models.Spt.Config;

/// <summary>
///     WebDAV 游戏更新源配置（mod 独立配置文件，取代 core.json）。
///     启动器经 <c>GET /launcher/webdav/config</c> 拉取本配置，连到该 WebDAV 列出/下载更新包 zip。
///     字段名（url/username/password/zipDirectory）与启动器 <c>WebDavSettings</c> 对齐；
///     启动器用 Newtonsoft 大小写不敏感反序列化，服务端按 camelCase 序列化即可。
///     文件：SPT_Data/webdav/config.json，不存在则写空模板供服主填写。
///     url 为空时启动器显示"未配置"，是预期的未配置态，不算错误。
/// </summary>
public record WebDavModConfig
{
    /// <summary>WebDAV 根 URL（如 https://dav.example.com/spt-updates/）。空 = 未配置。</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    /// <summary>WebDAV 用户名（匿名访问留空）。</summary>
    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    /// <summary>WebDAV 密码（匿名访问留空）。</summary>
    [JsonPropertyName("password")]
    public string Password { get; set; } = "";

    /// <summary>更新包 zip 所在的 WebDAV 子目录（相对 url；放根目录留空）。</summary>
    [JsonPropertyName("zipDirectory")]
    public string ZipDirectory { get; set; } = "";

    private static string ConfigPath =>
        Path.Combine(Directory.GetCurrentDirectory(), "SPT_Data", "webdav", "config.json");

    /// <summary>
    ///     读取配置；文件缺失时写一份空模板并返回它（url 为空，启动器据此显示"未配置"）。
    ///     任何解析异常都回落到空配置，绝不抛出——避免启动器侧因服务端 500 拿到 null 响应再 NRE。
    /// </summary>
    public static WebDavModConfig Load()
    {
        try
        {
            var path = ConfigPath;
            if (!File.Exists(path))
            {
                var template = new WebDavModConfig();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(
                    path,
                    JsonSerializer.Serialize(template, new JsonSerializerOptions { WriteIndented = true })
                );
                return template;
            }

            return JsonSerializer.Deserialize<WebDavModConfig>(File.ReadAllText(path)) ?? new WebDavModConfig();
        }
        catch
        {
            return new WebDavModConfig();
        }
    }
}
