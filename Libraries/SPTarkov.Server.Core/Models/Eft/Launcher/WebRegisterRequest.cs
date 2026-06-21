using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Utils;

namespace SPTarkov.Server.Core.Models.Eft.Launcher;

/// <summary>
/// Web 注册请求数据模型
/// 包含邮箱、验证码、用户名、密码和版本信息
/// </summary>
public record WebRegisterRequest : IRequestData
{
    /// <summary>
    /// 邮箱地址
    /// </summary>
    [JsonPropertyName("email")]
    public string? Email { get; set; }

    /// <summary>
    /// 验证码
    /// </summary>
    [JsonPropertyName("verificationCode")]
    public string? VerificationCode { get; set; }

    /// <summary>
    /// 用户名
    /// </summary>
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    /// <summary>
    /// 密码
    /// </summary>
    [JsonPropertyName("password")]
    public string? Password { get; set; }

    /// <summary>
    /// 游戏版本/版本类型
    /// </summary>
    [JsonPropertyName("edition")]
    public string? Edition { get; set; }

    /// <summary>
    /// 注册激活码（选填）：有效时锁定为码绑定的版本，注册成功后码失效
    /// </summary>
    [JsonPropertyName("activationCode")]
    public string? ActivationCode { get; set; }
}
