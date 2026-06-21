using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Spt.Logging;
using SPTarkov.Server.Core.Models.Utils;

namespace SPTarkov.Server.Core.Services;

/// <summary>
/// 验证码信息
/// </summary>
public record VerificationCodeInfo
{
    /// <summary>
    /// 验证码
    /// </summary>
    public required string Code { get; set; }

    /// <summary>
    /// 创建时间（时间戳）
    /// </summary>
    public required long CreatedTime { get; set; }

    /// <summary>
    /// 邮箱地址
    /// </summary>
    public required string Email { get; set; }
}

/// <summary>
/// 验证码存储服务
/// </summary>
[Injectable(InjectionType.Singleton)]
public class VerificationCodeService
{
    private readonly Dictionary<string, VerificationCodeInfo> _verificationCodes = new();
    private readonly ISptLogger<VerificationCodeService> _logger;

    /// <summary>
    /// 验证码有效期（分钟）
    /// </summary>
    private const int VERIFICATION_CODE_EXPIRY_MINUTES = 10;

    /// <summary>
    /// 验证码长度
    /// </summary>
    private const int VERIFICATION_CODE_LENGTH = 6;

    public VerificationCodeService(ISptLogger<VerificationCodeService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 生成验证码
    /// </summary>
    /// <param name="email">邮箱地址</param>
    /// <returns>验证码</returns>
    public string GenerateVerificationCode(string email)
    {
        // 清理过期的验证码
        CleanExpiredCodes();

        // 生成6位随机数字验证码
        var random = new Random();
        var code = new string(Enumerable.Repeat("0123456789", VERIFICATION_CODE_LENGTH)
            .Select(s => s[random.Next(s.Length)])
            .ToArray());

        // 存储验证码信息
        var codeInfo = new VerificationCodeInfo
        {
            Code = code,
            CreatedTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Email = email
        };

        _verificationCodes[email] = codeInfo;

        _logger.Debug($"为邮箱 {email} 生成验证码: {code}");

        return code;
    }

    /// <summary>
    /// 验证验证码
    /// </summary>
    /// <param name="email">邮箱地址</param>
    /// <param name="code">验证码</param>
    /// <returns>验证是否成功</returns>
    public bool VerifyCode(string email, string code)
    {
        // 清理过期的验证码
        CleanExpiredCodes();

        if (!_verificationCodes.TryGetValue(email, out var codeInfo))
        {
            _logger.Warning($"邮箱 {email} 没有有效的验证码");
            return false;
        }

        // 检查验证码是否过期
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expiryTime = codeInfo.CreatedTime + (VERIFICATION_CODE_EXPIRY_MINUTES * 60);

        if (currentTime > expiryTime)
        {
            _logger.Warning($"邮箱 {email} 的验证码已过期");
            _verificationCodes.Remove(email);
            return false;
        }

        // 验证验证码
        if (codeInfo.Code != code)
        {
            _logger.Warning($"邮箱 {email} 的验证码不匹配");
            return false;
        }

        // 验证成功，删除验证码
        _verificationCodes.Remove(email);
        _logger.Info($"邮箱 {email} 的验证码验证成功");

        return true;
    }

    /// <summary>
    /// 检查验证码是否存在且有效
    /// </summary>
    /// <param name="email">邮箱地址</param>
    /// <returns>是否存在有效验证码</returns>
    public bool HasValidCode(string email)
    {
        // 清理过期的验证码
        CleanExpiredCodes();

        if (!_verificationCodes.TryGetValue(email, out var codeInfo))
        {
            return false;
        }

        // 检查验证码是否过期
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expiryTime = codeInfo.CreatedTime + (VERIFICATION_CODE_EXPIRY_MINUTES * 60);

        return currentTime <= expiryTime;
    }

    /// <summary>
    /// 清理过期的验证码
    /// </summary>
    private void CleanExpiredCodes()
    {
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expiryTime = VERIFICATION_CODE_EXPIRY_MINUTES * 60;

        var expiredKeys = _verificationCodes
            .Where(kvp => currentTime > (kvp.Value.CreatedTime + expiryTime))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            _verificationCodes.Remove(key);
            _logger.Debug($"清理过期验证码: {key}");
        }
    }

    /// <summary>
    /// 获取验证码剩余有效时间（秒）
    /// </summary>
    /// <param name="email">邮箱地址</param>
    /// <returns>剩余有效时间（秒），如果不存在或已过期返回0</returns>
    public long GetRemainingTime(string email)
    {
        if (!_verificationCodes.TryGetValue(email, out var codeInfo))
        {
            return 0;
        }

        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expiryTime = codeInfo.CreatedTime + (VERIFICATION_CODE_EXPIRY_MINUTES * 60);
        var remainingTime = expiryTime - currentTime;

        return remainingTime > 0 ? remainingTime : 0;
    }
}
