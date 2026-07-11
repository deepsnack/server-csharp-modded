using System.Text.Json.Serialization;

namespace SPTarkov.Server.Core.Models.Spt.Config;

public record HttpConfig : BaseConfig
{
    [JsonPropertyName("kind")]
    public override string Kind { get; set; } = "spt-http";

    /// <summary>
    ///     Address used by webserver
    /// </summary>
    [JsonPropertyName("ip")]
    public required string Ip { get; set; }

    [JsonPropertyName("port")]
    public required int Port { get; set; }

    /// <summary>
    ///     Address used by game client to connect to
    /// </summary>
    [JsonPropertyName("backendIp")]
    public required string BackendIp { get; set; }

    [JsonPropertyName("backendPort")]
    public required int BackendPort { get; set; }

    [JsonPropertyName("logRequests")]
    public required bool LogRequests { get; set; }

    [JsonPropertyName("forwardedHeaders")]
    public ForwardedHeadersConfig ForwardedHeaders { get; set; } = new();

    /// <summary>
    ///     e.g. "SPT_Data/Server/images/traders/579dc571d53a0658a154fbec.png": "SPT_Data/Server/images/traders/NewTraderImage.png"
    /// </summary>
    [JsonPropertyName("serverImagePathOverride")]
    public required Dictionary<string, string> ServerImagePathOverride { get; set; }
}

public record ForwardedHeadersConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("forwardLimit")]
    public int ForwardLimit { get; set; } = 1;

    [JsonPropertyName("knownProxies")]
    public List<string> KnownProxies { get; set; } = ["127.0.0.1", "::1"];

    [JsonPropertyName("knownNetworks")]
    public List<string> KnownNetworks { get; set; } = [];
}
