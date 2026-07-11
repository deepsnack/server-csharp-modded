using System.Text.Json;
using System.Text.Json.Serialization;

namespace SPTarkov.Server.Core.Models.Spt.Config;

/// <summary>
///     SMTP 邮件配置（mod 本地副本，原在 CoreConfig）。required 改为可选 + 默认值，
///     便于从可能不完整的独立 json 反序列化；控制器对 null 已有判空。
/// </summary>
public record WebRegisterSmtpConfig
{
    [JsonPropertyName("server")]
    public string Server { get; set; } = "";

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("useSsl")]
    public bool UseSsl { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";

    [JsonPropertyName("senderEmail")]
    public string SenderEmail { get; set; } = "";

    [JsonPropertyName("senderName")]
    public string? SenderName { get; set; }

    [JsonPropertyName("timeoutMs")]
    public int TimeoutMs { get; set; } = 30000;

    [JsonPropertyName("verificationCodeExpiryMinutes")]
    public int VerificationCodeExpiryMinutes { get; set; } = 5;
}

/// <summary>Web 注册管理员配置（mod 本地副本，原在 CoreConfig）。</summary>
public record WebRegisterConfig
{
    [JsonPropertyName("adminPassword")]
    public string AdminPassword { get; set; } = "";

    /// <summary>
    ///     接收后台审核提醒的管理员邮箱。为空时，通行证审核提醒回退发送到 smtpConfig.senderEmail。
    /// </summary>
    [JsonPropertyName("adminNotificationEmails")]
    public List<string> AdminNotificationEmails { get; set; } = [];
}

/// <summary>
///     Portal 兼容 sidecar 配置：一个独立 http 监听端口，让 WebRegister 出现在 SptManagerPortal 卡片页。
///     仅承担 portal-info/sso 握手，真正的注册/管理功能仍在主服务器 https 上。详见 PortalBridgeService。
/// </summary>
public record PortalBridgeConfig
{
    /// <summary>是否启用 Portal 兼容 sidecar。默认 true；不需要 Portal 集成时可设 false 关闭这个额外端口。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>sidecar 监听端口（纯 http，供 Portal 探测/跳转）。默认 7793。</summary>
    [JsonPropertyName("port")]
    public int Port { get; set; } = 7793;

    /// <summary>
    ///     监听主机列表。"localhost"/"127.0.0.1" 仅本机（默认）；"+" 绑全网卡（需管理员或 netsh urlacl）；
    ///     或填本机内网 IP。公网部署需与主服务器一并放行此端口。
    /// </summary>
    [JsonPropertyName("hosts")]
    public List<string> Hosts { get; set; } = ["localhost", "127.0.0.1"];
}

/// <summary>
///     SPT-WebRegister 的独立配置（取代 core.json 的 smtpConfig/webRegisterConfig）。
///     文件：SPT_Data/webregister/config.json，不存在则写空模板供服主填写。
/// </summary>
public record WebRegisterModConfig
{
    [JsonPropertyName("smtpConfig")]
    public WebRegisterSmtpConfig? SmtpConfig { get; set; }

    [JsonPropertyName("webRegisterConfig")]
    public WebRegisterConfig? WebRegisterConfig { get; set; }

    /// <summary>
    ///     是否允许通过启动器 API 注册。默认 false（禁用启动器内注册，强制走网页注册），
    ///     取代原 CoreConfig.Features.AllowRegistration。
    /// </summary>
    [JsonPropertyName("allowLauncherRegistration")]
    public bool AllowLauncherRegistration { get; set; } = false;

    /// <summary>Portal 兼容 sidecar 配置（缺省时按默认值：启用、端口 7793、本机监听）。</summary>
    [JsonPropertyName("portalBridge")]
    public PortalBridgeConfig? PortalBridge { get; set; }

    private static string ConfigPath =>
        Path.Combine(Directory.GetCurrentDirectory(), "SPT_Data", "webregister", "config.json");

    public static WebRegisterModConfig Load()
    {
        try
        {
            var path = ConfigPath;
            if (!File.Exists(path))
            {
                var template = new WebRegisterModConfig
                {
                    SmtpConfig = new WebRegisterSmtpConfig(),
                    WebRegisterConfig = new WebRegisterConfig(),
                    PortalBridge = new PortalBridgeConfig(),
                };
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(
                    path,
                    JsonSerializer.Serialize(template, new JsonSerializerOptions { WriteIndented = true })
                );
                return template;
            }

            return JsonSerializer.Deserialize<WebRegisterModConfig>(File.ReadAllText(path)) ?? new WebRegisterModConfig();
        }
        catch
        {
            return new WebRegisterModConfig();
        }
    }
}
