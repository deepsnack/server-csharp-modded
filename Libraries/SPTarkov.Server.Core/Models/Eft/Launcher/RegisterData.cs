using System.Text.Json.Serialization;

namespace SPTarkov.Server.Core.Models.Eft.Launcher;

/// <summary>
/// 注册请求数据模型
/// 继承自 LoginRequestData，包含用户名、密码和版本信息
/// </summary>
public record RegisterData : LoginRequestData
{
    /// <summary>
    /// 游戏版本/版本类型
    /// </summary>
    [JsonPropertyName("edition")]
    public string? Edition { get; set; }
}
