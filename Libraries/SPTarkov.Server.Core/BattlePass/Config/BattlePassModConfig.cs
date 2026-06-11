using System.Text.Json;
using System.Text.Json.Serialization;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>
///     BattlePass Portal 兼容 sidecar 端口配置（独立于 WebRegister 的 portalBridge，
///     不再用 <c>WebRegister.port + 1</c> 推算）。文件：SPT_Data/battlepass/config.json。
/// </summary>
public record BattlePassPortalBridgeConfig
{
    /// <summary>是否启用 Portal 兼容 sidecar。默认 true。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>sidecar 监听端口（纯 http，供 Portal 探测/跳转）。默认 7794。</summary>
    [JsonPropertyName("port")]
    public int Port { get; set; } = 7794;

    /// <summary>
    ///     监听主机列表。"localhost"/"127.0.0.1" 仅本机（默认）；"+" 绑全网卡（需管理员或 netsh urlacl）；
    ///     或填本机内网 IP。
    /// </summary>
    [JsonPropertyName("hosts")]
    public List<string> Hosts { get; set; } = ["localhost", "127.0.0.1"];
}

/// <summary>
///     BattlePass 模块的独立配置（与 WebRegister 配置解耦）。
///     文件：SPT_Data/battlepass/config.json，不存在则写模板供服主填写。
/// </summary>
public record BattlePassModConfig
{
    /// <summary>Portal 兼容 sidecar 配置（缺省时按默认值：启用、端口 7794、本机监听）。</summary>
    [JsonPropertyName("portalBridge")]
    public BattlePassPortalBridgeConfig? PortalBridge { get; set; }

    private static BattlePassModConfig? CurrentConfig;

    private static string ConfigPath =>
        Path.Combine(Directory.GetCurrentDirectory(), "SPT_Data", "battlepass", "config.json");

    /// <summary>
    ///     读入配置；若文件不存在则自动写出含默认值的 JSON 模板，供服主按需修改端口/hosts/enabled。
    ///     序列化异常时降级为空配置（运行时不中断），上层调用处对 null 均有判空。
    /// </summary>
    public static BattlePassModConfig Load()
    {
        try
        {
            var path = ConfigPath;
            if (!File.Exists(path))
            {
                var template = new BattlePassModConfig
                {
                    PortalBridge = new BattlePassPortalBridgeConfig(),
                };
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(
                    path,
                    JsonSerializer.Serialize(template, new JsonSerializerOptions { WriteIndented = true })
                );
                CurrentConfig = template;
                return template;
            }

            CurrentConfig = JsonSerializer.Deserialize<BattlePassModConfig>(File.ReadAllText(path))
                            ?? new BattlePassModConfig();
            return CurrentConfig;
        }
        catch
        {
            CurrentConfig = new BattlePassModConfig();
            return CurrentConfig;
        }
    }
}
