using System.Text.Json.Serialization;

namespace SPTarkov.Server.Core.Models.Eft.Launcher;

/// <summary>
/// Web 注册响应数据模型
/// 包含注册结果和相关信息
/// </summary>
public record WebRegisterResponse
{
    /// <summary>
    /// 是否成功
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>
    /// 消息
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>
    /// 用户 ID（成功时返回）
    /// </summary>
    [JsonPropertyName("userId")]
    public string? UserId { get; set; }
}
