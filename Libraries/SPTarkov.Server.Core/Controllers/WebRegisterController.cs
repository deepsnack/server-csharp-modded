using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Microsoft.AspNetCore.Mvc;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Launcher;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;

namespace SPTarkov.Server.Core.Controllers;

/// <summary>
/// Web 注册控制器
/// 处理 Web 端的用户注册相关请求
/// </summary>
[Injectable]
[ApiController]
[Route("register/api")]
public class WebRegisterController(
    HttpResponseUtil httpResponseUtil,
    SaveServer saveServer,
    HashUtil hashUtil,
    Services.RegisterActivationCodeService activationCodeService,
    Services.LastLoginService lastLoginService,
    ISptLogger<WebRegisterController> logger
)
{
    // 获取SMTP配置
    private readonly WebRegisterModConfig _modConfig = WebRegisterModConfig.Load();

    // 验证码存储（邮箱 -> 验证码）
    private static readonly Dictionary<string, string> VerificationCodes = new();

    // 验证码过期时间（邮箱 -> 过期时间）
    private static readonly Dictionary<string, DateTime> VerificationCodeExpiry = new();

    // 管理员 token（server 重启即失效；浏览器关闭后 sessionStorage 清空，客户端不会再发送旧 token）
    private static readonly ConcurrentDictionary<string, byte> AdminTokens = new();

    /// <summary>
    /// 签发并登记一个管理员 token，返回给调用方。供 Portal 兼容 sidecar（同进程）在验证 Portal SSO 短 token 后
    /// 复用同一鉴权域签发 admin 登录态——admin 页用 X-Admin-Token 携带，<see cref="IsAdminAuthorized"/> 据此放行。
    /// server 重启即失效，与 admin/login 路径同源。
    /// </summary>
    public static string IssueAdminToken()
    {
        var token = Guid.NewGuid().ToString("N");
        AdminTokens.TryAdd(token, 0);
        return token;
    }

    // 验证码有效期（分钟）：读取 core.json 的 smtpConfig.verificationCodeExpiryMinutes，缺省/非法时回退 5
    private int VerificationCodeExpiryMinutes
    {
        get
        {
            var minutes = _modConfig.SmtpConfig?.VerificationCodeExpiryMinutes ?? 5;
            return minutes > 0 ? minutes : 5;
        }
    }

    // 已注册邮箱文件路径（存储在与 database 分离的目录，避免 DatabaseImporter 扫描）
    private static string RegisteredEmailsFilePath => Path.Combine(Directory.GetCurrentDirectory(), "SPT_Data", "webregister", "registered_emails.json");

    // 邮箱用户名映射文件路径
    private static string EmailMappingFilePath => Path.Combine(Directory.GetCurrentDirectory(), "SPT_Data", "webregister", "email_mapping.json");

    // 管理员版本白名单配置路径
    private static string AdminConfigFilePath => Path.Combine(Directory.GetCurrentDirectory(), "SPT_Data", "webregister", "admin_config.json");

    // 预注册列表路径（email → 锁定版本）
    private static string PreRegisteredFilePath => Path.Combine(Directory.GetCurrentDirectory(), "SPT_Data", "webregister", "pre_registered.json");

    /// <summary>
    /// 获取可用的版本列表
    /// 从 SP-T_Data/database/templates/profiles.json 读取版本模板信息
    /// 如果文件改动，注册页会自动显示更新后的版本列表
    /// </summary>
    /// <returns>版本列表</returns>
    [HttpGet("versions")]
    public object GetVersions()
    {
        try
        {
            var profilesJsonPath = Path.Combine(Directory.GetCurrentDirectory(), "SPT_Data", "database", "templates", "profiles.json");

            if (!File.Exists(profilesJsonPath))
            {
                return new { success = false, message = "profiles.json 不存在" };
            }

            var content = File.ReadAllText(profilesJsonPath);
            using var document = JsonDocument.Parse(content);
            
            var allVersions = new List<string>();

            // profiles.json 的每个 key 就是一个版本
            foreach (var property in document.RootElement.EnumerateObject())
            {
                allVersions.Add(property.Name);
            }

            // 若管理员配置了白名单，只返回白名单内的版本（保持 profiles.json 中的原始顺序）
            var allowedVersions = LoadAllowedVersions();
            var versions = allowedVersions.Count > 0
                ? allVersions.Where(v => allowedVersions.Contains(v)).ToList()
                : allVersions;

            return new
            {
                success = true,
                versions
            };
        }
        catch (Exception ex)
        {
            return new { success = false, message = $"获取版本列表失败: {ex.Message}" };
        }
    }

    /// <summary>
    /// 发送验证码到指定邮箱
    /// </summary>
    /// <param name="request">包含邮箱的请求</param>
    /// <returns>发送结果</returns>
    [HttpPost("send-verification-code")]
    public object SendVerificationCode([FromBody] System.Text.Json.JsonElement request)
    {
        try
        {
            string? email = request.TryGetProperty("email", out var emailProp)
                ? emailProp.GetString()
                : null;

            if (string.IsNullOrEmpty(email))
            {
                return new { success = false, message = "邮箱地址不能为空" };
            }

            // 验证邮箱格式
            if (!IsValidEmail(email))
            {
                return new { success = false, message = "邮箱格式不正确" };
            }

            // 生成 6 位数字验证码
            var verificationCode = GenerateVerificationCode();

            // 存储验证码和过期时间
            VerificationCodes[email] = verificationCode;
            VerificationCodeExpiry[email] = DateTime.Now.AddMinutes(VerificationCodeExpiryMinutes);

            // 获取SMTP配置
            var smtpConfig = _modConfig.SmtpConfig;

            // 发送邮件（模拟真实生产环境：未配置 SMTP 视为故障，不再走测试模式假成功）
            if (smtpConfig == null || string.IsNullOrEmpty(smtpConfig.Server))
            {
                // SmtpConfig 为 null 通常意味着 config.json 缺失或解析失败（Load() 已吞异常降级为空配置）
                logger.Error("[WebRegister] SMTP 未配置或 config.json 解析失败，无法发送验证码。请检查 SPT_Data/webregister/config.json");
                return new { success = false, message = "邮件服务未配置，无法发送验证码，请联系管理员" };
            }

            SendEmailWithSmtp(smtpConfig, email, verificationCode);
            return new
            {
                success = true,
                message = "验证码已发送到您的邮箱"
            };
        }
        catch (Exception ex)
        {
            return new { success = false, message = $"发送验证码失败: {ex.Message}" };
        }
    }

    /// <summary>
    /// 使用SMTP发送邮件
    /// </summary>
    private void SendEmailWithSmtp(WebRegisterSmtpConfig smtpConfig, string toEmail, string verificationCode, string subject = "SPT 注册验证码", string purpose = "注册")
    {
        var senderName = string.IsNullOrEmpty(smtpConfig.SenderName) ? "SPT注册验证" : smtpConfig.SenderName;

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(senderName, smtpConfig.SenderEmail));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = subject;
        message.Body = new TextPart("html")
        {
            Text = $@"<html>
<body style=""font-family: Arial, sans-serif; padding: 20px;"">
    <div style=""max-width: 600px; margin: 0 auto; padding: 20px; border: 1px solid #ddd; border-radius: 8px;"">
        <h2 style=""color: #333;"">{subject}</h2>
        <p style=""font-size: 16px; color: #555;"">您好，</p>
        <p style=""font-size: 16px; color: #555;"">您的{purpose}验证码是：</p>
        <div style=""font-size: 32px; font-weight: bold; color: #667eea; padding: 15px; background: #f5f5f5; border-radius: 8px; text-align: center; letter-spacing: 5px; margin: 20px 0;"">
            {verificationCode}
        </div>
        <p style=""font-size: 14px; color: #999;"">验证码有效期为 {VerificationCodeExpiryMinutes} 分钟，请尽快完成{purpose}。</p>
        <p style=""font-size: 14px; color: #999; margin-top: 20px;"">如果这不是您的操作，请忽略此邮件。</p>
    </div>
</body>
</html>"
        };

        // Port 465 = implicit SSL (SecureSocketOptions.SslOnConnect)
        // Port 587 = STARTTLS (SecureSocketOptions.StartTls)
        var socketOptions = smtpConfig.Port == 465
            ? SecureSocketOptions.SslOnConnect
            : smtpConfig.UseSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None;

        using var client = new SmtpClient();
        client.Timeout = smtpConfig.TimeoutMs > 0 ? smtpConfig.TimeoutMs : 30000;
        client.Connect(smtpConfig.Server, smtpConfig.Port, socketOptions);
        client.Authenticate(smtpConfig.Username, smtpConfig.Password);
        client.Send(message);
        client.Disconnect(true);
        logger.Info($"[WebRegister] Verification code sent via SMTP to: {toEmail}");
    }

    /// <summary>
    /// 找回密码：向【已注册】邮箱发送验证码。邮箱未注册时明确返回提示（与注册流程相反——注册要求邮箱未被占用）。
    /// </summary>
    [HttpPost("send-reset-code")]
    public object SendResetCode([FromBody] System.Text.Json.JsonElement request)
    {
        try
        {
            string? email = request.TryGetProperty("email", out var emailProp)
                ? emailProp.GetString()
                : null;

            if (string.IsNullOrEmpty(email))
            {
                return new { success = false, message = "邮箱地址不能为空" };
            }

            if (!IsValidEmail(email))
            {
                return new { success = false, message = "邮箱格式不正确" };
            }

            // 找回密码要求邮箱必须已注册（与注册相反）
            if (!IsEmailRegistered(email))
            {
                return new { success = false, message = "该邮箱未注册" };
            }

            var verificationCode = GenerateVerificationCode();
            VerificationCodes[email] = verificationCode;
            VerificationCodeExpiry[email] = DateTime.Now.AddMinutes(VerificationCodeExpiryMinutes);

            var smtpConfig = _modConfig.SmtpConfig;
            if (smtpConfig == null || string.IsNullOrEmpty(smtpConfig.Server))
            {
                logger.Error("[WebRegister] SMTP 未配置或 config.json 解析失败，无法发送找回密码验证码。请检查 SPT_Data/webregister/config.json");
                return new { success = false, message = "邮件服务未配置，无法发送验证码，请联系管理员" };
            }

            SendEmailWithSmtp(smtpConfig, email, verificationCode, "SPT 找回密码验证码", "找回密码");
            return new { success = true, message = "验证码已发送到您的邮箱" };
        }
        catch (Exception ex)
        {
            return new { success = false, message = $"发送验证码失败: {ex.Message}" };
        }
    }

    /// <summary>
    /// 找回密码：校验邮箱已注册 + 验证码正确后，重设该账户密码（写入 PasswordAuth 的 passwords.json，与登录同源）。
    /// </summary>
    [HttpPost("reset-password")]
    public object ResetPassword([FromBody] System.Text.Json.JsonElement request)
    {
        try
        {
            string? email = request.TryGetProperty("email", out var e) ? e.GetString() : null;
            string? code = request.TryGetProperty("verificationCode", out var c) ? c.GetString() : null;
            string? newPassword = request.TryGetProperty("newPassword", out var p) ? p.GetString() : null;

            if (string.IsNullOrEmpty(email))
            {
                return new { success = false, message = "邮箱地址不能为空" };
            }

            if (string.IsNullOrEmpty(code))
            {
                return new { success = false, message = "验证码不能为空" };
            }

            if (string.IsNullOrEmpty(newPassword) || newPassword.Length < 6)
            {
                return new { success = false, message = "密码至少需要6个字符" };
            }

            if (!IsValidEmail(email))
            {
                return new { success = false, message = "邮箱格式不正确" };
            }

            if (!IsEmailRegistered(email))
            {
                return new { success = false, message = "该邮箱未注册" };
            }

            if (!VerifyCode(email, code))
            {
                return new { success = false, message = "验证码无效或已过期" };
            }

            // 邮箱已注册但定位不到账户（如映射缺失/存档被手动删除），无法重设
            var profileId = GetProfileIdByEmail(email);
            if (profileId is null || profileId.Value.IsEmpty)
            {
                return new { success = false, message = "无法定位账户，请联系管理员" };
            }

            // 密码写入存档 Info.Password（树内统一存储；哈希算法与登录校验同源）
            var resetProfile = saveServer.GetProfile(profileId.Value);
            if (resetProfile?.ProfileInfo is null)
            {
                return new { success = false, message = "无法定位账户，请联系管理员" };
            }

            resetProfile.ProfileInfo.Password = EncryptPassword(newPassword);
            saveServer.SaveProfileAsync(profileId.Value).GetAwaiter().GetResult();

            // 清除已使用的验证码
            VerificationCodes.Remove(email);
            VerificationCodeExpiry.Remove(email);

            logger.Info($"[WebRegister] 密码已重置 profileId={profileId.Value} (email={email})");
            return new { success = true, message = "密码重置成功，请使用新密码登录" };
        }
        catch (Exception ex)
        {
            return new { success = false, message = $"重置密码失败: {ex.Message}" };
        }
    }

    /// <summary>
    /// 按邮箱反查账户 profileId：email_mapping(username→email) 反查用户名，再从内存 profile 表按用户名取 profileId。
    /// 找不到返回 null。
    /// </summary>
    private MongoId? GetProfileIdByEmail(string email)
    {
        try
        {
            var normalized = email.ToLowerInvariant();
            var mappings = LoadEmailMapping(); // username -> email(lowercased)
            var username = mappings.FirstOrDefault(kv => kv.Value == normalized).Key;
            if (string.IsNullOrEmpty(username))
            {
                return null;
            }

            foreach (var kv in saveServer.GetProfiles())
            {
                if (kv.Value.ProfileInfo?.Username == username)
                {
                    return kv.Key;
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 处理用户注册
    /// </summary>
    /// <param name="request">注册请求</param>
    /// <returns>注册结果</returns>
    [HttpPost("register")]
    public async Task<object> Register([FromBody] WebRegisterRequest request)
    {
        try
        {
            // 验证必填字段
            if (string.IsNullOrEmpty(request.Email))
            {
                return new WebRegisterResponse
                {
                    Success = false,
                    Message = "邮箱地址不能为空"
                };
            }

            if (string.IsNullOrEmpty(request.VerificationCode))
            {
                return new WebRegisterResponse
                {
                    Success = false,
                    Message = "验证码不能为空"
                };
            }

            if (string.IsNullOrEmpty(request.Username))
            {
                return new WebRegisterResponse
                {
                    Success = false,
                    Message = "用户名不能为空"
                };
            }

            if (string.IsNullOrEmpty(request.Password))
            {
                return new WebRegisterResponse
                {
                    Success = false,
                    Message = "密码不能为空"
                };
            }

            if (string.IsNullOrEmpty(request.Edition))
            {
                return new WebRegisterResponse
                {
                    Success = false,
                    Message = "版本不能为空"
                };
            }

            // 验证邮箱格式
            if (!IsValidEmail(request.Email))
            {
                return new WebRegisterResponse
                {
                    Success = false,
                    Message = "邮箱格式不正确"
                };
            }

            // 检查邮箱是否已被注册
            if (IsEmailRegistered(request.Email))
            {
                return new WebRegisterResponse
                {
                    Success = false,
                    Message = "该邮箱已被注册"
                };
            }

            // 验证验证码
            if (!VerifyCode(request.Email, request.VerificationCode))
            {
                return new WebRegisterResponse
                {
                    Success = false,
                    Message = "验证码无效或已过期"
                };
            }

            // 激活码（N2）：优先级最高，有效时锁定为码绑定的版本（可为普通用户不可见的版本），
            // 先原子占用（防并发重复用），注册失败时回滚。仍需邮箱验证（上方已校验）。
            var activationCode = request.ActivationCode?.Trim();
            var usingActivationCode = !string.IsNullOrEmpty(activationCode);
            if (usingActivationCode)
            {
                var (claimed, codeEdition, codeMessage) = activationCodeService.TryRedeem(
                    activationCode!,
                    request.Email!,
                    request.Username!
                );
                if (!claimed)
                {
                    return new WebRegisterResponse { Success = false, Message = codeMessage };
                }

                request = request with { Edition = codeEdition };
            }

            // 检查是否为预注册用户；若命中则强制使用锁定版本，忽略客户端传来的版本值（激活码优先级更高）
            var preRegistrations = LoadPreRegistrations();
            var emailKey = request.Email!.ToLowerInvariant();
            var isPreRegistered = preRegistrations.TryGetValue(emailKey, out var lockedVersion);
            if (usingActivationCode)
            {
                // 版本已由激活码锁定，跳过预注册/白名单判断
            }
            else if (isPreRegistered)
            {
                request = request with { Edition = lockedVersion };
            }
            else
            {
                // 普通玩家：验证所选版本是否在白名单内（白名单为空则允许全部）
                var allowedVersions = LoadAllowedVersions();
                if (allowedVersions.Count > 0 && !allowedVersions.Contains(request.Edition!))
                {
                    return new WebRegisterResponse
                    {
                        Success = false,
                        Message = "所选版本不可用"
                    };
                }
            }

            // 检查用户名是否已存在；仅当磁盘上确有该 profile 才算占用，
            // 否则视为残留的幽灵索引（手动删档或注册中途失败留下），自动清理并放行
            if (saveServer.GetProfiles().Values.Any(p => p.ProfileInfo?.Username == request.Username))
            {
                if (usingActivationCode)
                {
                    activationCodeService.ReleaseCode(activationCode!);
                }

                return new WebRegisterResponse
                {
                    Success = false,
                    Message = "用户名已存在"
                };
            }

            // 创建用户账户
            var profileId = await CreateAccount(request);

            if (profileId.IsEmpty)
            {
                if (usingActivationCode)
                {
                    activationCodeService.ReleaseCode(activationCode!);
                }

                return new WebRegisterResponse
                {
                    Success = false,
                    Message = "创建账户失败"
                };
            }

            // 清除已使用的验证码
            VerificationCodes.Remove(request.Email);
            VerificationCodeExpiry.Remove(request.Email);

            // 预注册用户完成注册后从预注册列表移除
            if (isPreRegistered)
            {
                preRegistrations.Remove(emailKey);
                SavePreRegistrations(preRegistrations);
            }

            // 将邮箱添加到已注册列表
            AddRegisteredEmail(request.Email);

            // 保存用户名到邮箱的映射
            AddEmailMapping(request.Username, request.Email);

            return new WebRegisterResponse
            {
                Success = true,
                Message = "注册成功",
                UserId = profileId.ToString()
            };
        }
        catch (Exception ex)
        {
            return new WebRegisterResponse
            {
                Success = false,
                Message = $"注册失败: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// 验证邮箱格式
    /// </summary>
    /// <param name="email">邮箱地址</param>
    /// <returns>是否有效</returns>
    private bool IsValidEmail(string email)
    {
        try
        {
            var addr = new System.Net.Mail.MailAddress(email);
            return addr.Address == email;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 生成 6 位数字验证码
    /// </summary>
    /// <returns>验证码</returns>
    private string GenerateVerificationCode()
    {
        var random = new Random();
        return random.Next(100000, 999999).ToString();
    }

    /// <summary>
    /// 验证验证码
    /// </summary>
    /// <param name="email">邮箱地址</param>
    /// <param name="code">验证码</param>
    /// <returns>是否有效</returns>
    private bool VerifyCode(string email, string code)
    {
        // 检查验证码是否存在
        if (!VerificationCodes.TryGetValue(email, out var storedCode))
        {
            return false;
        }

        // 检查验证码是否匹配
        if (storedCode != code)
        {
            return false;
        }

        // 检查验证码是否过期
        if (!VerificationCodeExpiry.TryGetValue(email, out var expiry))
        {
            return false;
        }

        if (DateTime.Now > expiry)
        {
            // 清除过期的验证码
            VerificationCodes.Remove(email);
            VerificationCodeExpiry.Remove(email);
            return false;
        }

        return true;
    }

    /// <summary>
    /// 创建用户账户
    /// </summary>
    /// <param name="request">注册请求</param>
    /// <returns>用户 ID</returns>
    private async Task<MongoId> CreateAccount(WebRegisterRequest request)
    {
        var profileId = new MongoId();
        try
        {
            var scavId = new MongoId();

            var newProfileDetails = new Info
            {
                ProfileId = profileId,
                ScavengerId = scavId,
                Aid = hashUtil.GenerateAccountId(),
                Username = request.Username,
                // 密码写入存档 Info.Password（树内统一存储，哈希算法与登录校验同源）
                Password = EncryptPassword(request.Password!),
                IsWiped = true,
                Edition = request.Edition
            };

            saveServer.CreateProfile(newProfileDetails);
            await saveServer.LoadProfileAsync(profileId);
            await saveServer.SaveProfileAsync(profileId);

            return profileId;
        }
        catch (Exception ex)
        {
            logger.Error($"[WebRegister] 创建账户失败 username='{request.Username}': {ex.Message}", ex);
            // 清理内存中的半成品 profile，防止 usernameIndex 永久残留该用户名（force 绕过软重置）
            try { saveServer.RemoveProfile(profileId, force: true); } catch { }
            return MongoId.Empty();
        }
    }

    /// <summary>
    /// 使用 SHA256 算法对密码进行加密
    /// </summary>
    /// <param name="password">原始密码</param>
    /// <returns>加密后的密码（十六进制字符串）</returns>
    private string EncryptPassword(string password)
    {
        // 使用 SHA256 算法创建哈希对象
        using var sha256 = SHA256.Create();

        // 将密码字符串转换为字节数组
        var passwordBytes = Encoding.UTF8.GetBytes(password);

        // 计算密码的哈希值
        var hashBytes = sha256.ComputeHash(passwordBytes);

        // 将哈希字节数组转换为十六进制字符串
        var hashString = BitConverter.ToString(hashBytes).Replace("-", string.Empty);

        return hashString;
    }

    /// <summary>
    /// 检查邮箱是否已被注册
    /// </summary>
    private bool IsEmailRegistered(string email)
    {
        try
        {
            var emails = LoadRegisteredEmails();
            return emails.Contains(email.ToLowerInvariant());
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 添加邮箱到已注册列表
    /// </summary>
    private void AddRegisteredEmail(string email)
    {
        try
        {
            var emails = LoadRegisteredEmails();
            var normalizedEmail = email.ToLowerInvariant();
            
            if (!emails.Contains(normalizedEmail))
            {
                emails.Add(normalizedEmail);
                SaveRegisteredEmails(emails);
            }
        }
        catch (Exception ex)
        {
            logger.Error($"[WebRegister] Failed to add registered email: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 从已注册列表中删除邮箱
    /// </summary>
    private bool RemoveRegisteredEmail(string email)
    {
        try
        {
            var emails = LoadRegisteredEmails();
            var normalizedEmail = email.ToLowerInvariant();
            
            if (emails.Remove(normalizedEmail))
            {
                SaveRegisteredEmails(emails);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            logger.Error($"[WebRegister] Failed to remove registered email: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// 加载已注册邮箱列表
    /// </summary>
    private List<string> LoadRegisteredEmails()
    {
        try
        {
            if (!File.Exists(RegisteredEmailsFilePath))
            {
                return new List<string>();
            }

            var content = File.ReadAllText(RegisteredEmailsFilePath);
            using var document = JsonDocument.Parse(content);
            
            var emails = new List<string>();
            if (document.RootElement.TryGetProperty("emails", out var emailsElement))
            {
                foreach (var email in emailsElement.EnumerateArray())
                {
                    emails.Add(email.GetString()!.ToLowerInvariant());
                }
            }
            return emails;
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// 保存已注册邮箱列表
    /// </summary>
    private void SaveRegisteredEmails(List<string> emails)
    {
        try
        {
            var directory = Path.GetDirectoryName(RegisteredEmailsFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var data = new { emails = emails };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(RegisteredEmailsFilePath, json);
        }
        catch (Exception ex)
        {
            logger.Error($"[WebRegister] Failed to save registered emails list: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 删除已注册邮箱（当用户删除存档时调用）
    /// </summary>
    /// <param name="request">包含邮箱的请求</param>
    /// <returns>删除结果</returns>
    [HttpPost("unregister-email")]
    public object UnregisterEmail([FromBody] System.Text.Json.JsonElement request)
    {
        try
        {
            string? email = request.TryGetProperty("email", out var emailProp)
                ? emailProp.GetString()
                : null;

            if (string.IsNullOrEmpty(email))
            {
                return new { success = false, message = "邮箱地址不能为空" };
            }

            if (RemoveRegisteredEmail(email))
            {
                return new { success = true, message = "邮箱记录已删除" };
            }
            else
            {
                return new { success = false, message = "邮箱记录不存在" };
            }
        }
        catch (Exception ex)
        {
            return new { success = false, message = $"删除邮箱记录失败: {ex.Message}" };
        }
    }

    /// <summary>
    /// 删除已注册邮箱（通过用户名）
    /// </summary>
    /// <param name="request">包含用户名的请求</param>
    /// <returns>删除结果</returns>
    [HttpPost("unregister-email-by-username")]
    public object UnregisterEmailByUsername([FromBody] System.Text.Json.JsonElement request)
    {
        try
        {
            string? username = request.TryGetProperty("username", out var usernameProp)
                ? usernameProp.GetString()
                : null;

            if (string.IsNullOrEmpty(username))
            {
                return new { success = false, message = "用户名不能为空" };
            }

            // 从映射文件中获取邮箱
            string? email = GetEmailByUsername(username);

            if (string.IsNullOrEmpty(email))
            {
                return new { success = false, message = "未找到该用户对应的邮箱记录" };
            }

            if (RemoveRegisteredEmail(email))
            {
                // 同时删除映射记录
                RemoveEmailMapping(username);
                return new { success = true, message = "邮箱记录已删除" };
            }
            else
            {
                return new { success = false, message = "邮箱记录不存在" };
            }
        }
        catch (Exception ex)
        {
            return new { success = false, message = $"删除邮箱记录失败: {ex.Message}" };
        }
    }

    /// <summary>
    /// 加载邮箱映射
    /// </summary>
    private Dictionary<string, string> LoadEmailMapping()
    {
        try
        {
            if (!File.Exists(EmailMappingFilePath))
            {
                return new Dictionary<string, string>();
            }

            var content = File.ReadAllText(EmailMappingFilePath);
            using var document = JsonDocument.Parse(content);
            
            var mappings = new Dictionary<string, string>();
            if (document.RootElement.TryGetProperty("mappings", out var mappingsElement))
            {
                foreach (var prop in mappingsElement.EnumerateObject())
                {
                    mappings[prop.Name] = prop.Value.GetString()!;
                }
            }
            return mappings;
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// 保存邮箱映射
    /// </summary>
    private void SaveEmailMapping(Dictionary<string, string> mappings)
    {
        try
        {
            var directory = Path.GetDirectoryName(EmailMappingFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var data = new { mappings = mappings };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(EmailMappingFilePath, json);
        }
        catch (Exception ex)
        {
            logger.Error($"[WebRegister] Failed to save email mapping: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 添加邮箱映射
    /// </summary>
    private void AddEmailMapping(string username, string email)
    {
        try
        {
            var mappings = LoadEmailMapping();
            mappings[username] = email.ToLowerInvariant();
            SaveEmailMapping(mappings);
        }
        catch (Exception ex)
        {
            logger.Error($"[WebRegister] Failed to add email mapping: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 删除邮箱映射
    /// </summary>
    private bool RemoveEmailMapping(string username)
    {
        try
        {
            var mappings = LoadEmailMapping();
            if (mappings.Remove(username))
            {
                SaveEmailMapping(mappings);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            logger.Error($"[WebRegister] Failed to remove email mapping: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// 通过用户名获取邮箱
    /// </summary>
    private string? GetEmailByUsername(string username)
    {
        try
        {
            var mappings = LoadEmailMapping();
            return mappings.TryGetValue(username, out var email) ? email : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 通过SessionId删除已注册的邮箱（当用户的存档被删除时调用）
    /// </summary>
    /// <param name="sessionId">用户SessionId</param>
    /// <returns>删除结果</returns>
    public object UnregisterEmailBySessionId(MongoId sessionId)
    {
        try
        {
            // 通过 Header 索引按 sessionId 反查 username (不触发完整 profile materialize)
            var usernameToRemove = saveServer.GetProfiles().TryGetValue(sessionId, out var profileForRemoval) ? profileForRemoval.ProfileInfo?.Username : null;

            if (string.IsNullOrEmpty(usernameToRemove))
            {
                // profile可能已经被删除，尝试从映射文件中查找
                // 由于sessionId和用户名没有直接映射，这里返回一个消息说明需要手动处理
                return new { success = false, message = "无法找到对应的用户信息" };
            }

            // 获取邮箱
            var email = GetEmailByUsername(usernameToRemove);

            // 删除邮箱记录
            if (!string.IsNullOrEmpty(email))
            {
                RemoveRegisteredEmail(email);
                RemoveEmailMapping(usernameToRemove);
                logger.Info($"[WebRegister] Removed email record for user {usernameToRemove} ({email})");
            }

            return new { success = true, message = "Email record removed" };
        }
        catch (Exception ex)
        {
            return new { success = false, message = $"Failed to remove email record: {ex.Message}" };
        }
    }

    // ==============================
    // 公开端点：预注册检查（供注册页 email blur 调用）
    // ==============================

    /// <summary>
    /// 检查邮箱是否在预注册列表中，返回是否预注册及锁定版本
    /// </summary>
    [HttpPost("check-preregistered")]
    public object CheckPreRegistered([FromBody] System.Text.Json.JsonElement request)
    {
        try
        {
            var email = request.TryGetProperty("email", out var e) ? e.GetString() : null;
            if (string.IsNullOrEmpty(email))
                return new { preRegistered = false, lockedVersion = (string?)null };

            var registrations = LoadPreRegistrations();
            var key = email.ToLowerInvariant();
            if (registrations.TryGetValue(key, out var version))
                return new { preRegistered = true, lockedVersion = version };

            return new { preRegistered = false, lockedVersion = (string?)null };
        }
        catch
        {
            return new { preRegistered = false, lockedVersion = (string?)null };
        }
    }

    // ==============================
    // 管理员端点
    // ==============================

    /// <summary>
    /// 验证管理员密码，成功返回 token
    /// </summary>
    [HttpPost("admin/login")]
    public object AdminLogin([FromBody] System.Text.Json.JsonElement request)
    {
        try
        {
            var password = request.TryGetProperty("password", out var p) ? p.GetString() : null;
            var adminPassword = _modConfig.WebRegisterConfig?.AdminPassword ?? "";

            if (string.IsNullOrEmpty(adminPassword))
                return new { success = false, message = "管理员功能未配置密码" };

            if (password != adminPassword)
                return new { success = false, message = "密码错误" };

            var token = Guid.NewGuid().ToString("N");
            AdminTokens.TryAdd(token, 0);
            return new { success = true, token };
        }
        catch (Exception ex)
        {
            return new { success = false, message = ex.Message };
        }
    }

    /// <summary>
    /// 获取 profiles.json 中的全部版本（不过滤）
    /// </summary>
    [HttpGet("admin/all-versions")]
    public object AdminGetAllVersions([FromHeader(Name = "X-Admin-Token")] string? adminToken = null)
    {
        if (!IsAdminAuthorized(adminToken))
            return new { success = false, message = "未授权" };

        try
        {
            var profilesJsonPath = Path.Combine(Directory.GetCurrentDirectory(), "SPT_Data", "database", "templates", "profiles.json");
            if (!File.Exists(profilesJsonPath))
                return new { success = false, message = "profiles.json 不存在" };

            var content = File.ReadAllText(profilesJsonPath);
            using var document = JsonDocument.Parse(content);
            var versions = document.RootElement.EnumerateObject().Select(p => p.Name).ToList();
            return new { success = true, versions };
        }
        catch (Exception ex)
        {
            return new { success = false, message = ex.Message };
        }
    }

    /// <summary>
    /// 获取当前允许版本白名单
    /// </summary>
    [HttpGet("admin/config")]
    public object AdminGetConfig([FromHeader(Name = "X-Admin-Token")] string? adminToken = null)
    {
        if (!IsAdminAuthorized(adminToken))
            return new { success = false, message = "未授权" };

        return new { success = true, allowedVersions = LoadAllowedVersions() };
    }

    /// <summary>
    /// 更新允许版本白名单
    /// </summary>
    [HttpPost("admin/config")]
    public object AdminSaveConfig([FromBody] System.Text.Json.JsonElement request, [FromHeader(Name = "X-Admin-Token")] string? adminToken = null)
    {
        if (!IsAdminAuthorized(adminToken))
            return new { success = false, message = "未授权" };

        try
        {
            var versions = new List<string>();
            if (request.TryGetProperty("allowedVersions", out var arr))
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var v = item.GetString();
                    if (!string.IsNullOrEmpty(v)) versions.Add(v);
                }
            }
            SaveAllowedVersions(versions);
            return new { success = true };
        }
        catch (Exception ex)
        {
            return new { success = false, message = ex.Message };
        }
    }

    /// <summary>
    /// 获取预注册列表
    /// </summary>
    [HttpGet("admin/preregistrations")]
    public object AdminGetPreRegistrations([FromHeader(Name = "X-Admin-Token")] string? adminToken = null)
    {
        if (!IsAdminAuthorized(adminToken))
            return new { success = false, message = "未授权" };

        return new { success = true, registrations = LoadPreRegistrations() };
    }

    /// <summary>
    /// 添加预注册（email → 锁定版本）
    /// </summary>
    [HttpPost("admin/preregistrations")]
    public object AdminAddPreRegistration([FromBody] System.Text.Json.JsonElement request, [FromHeader(Name = "X-Admin-Token")] string? adminToken = null)
    {
        if (!IsAdminAuthorized(adminToken))
            return new { success = false, message = "未授权" };

        try
        {
            var email = request.TryGetProperty("email", out var e) ? e.GetString()?.ToLowerInvariant() : null;
            var version = request.TryGetProperty("version", out var v) ? v.GetString() : null;

            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(version))
                return new { success = false, message = "email 和 version 不能为空" };

            if (IsEmailRegistered(email))
                return new { success = false, message = "该邮箱已完成注册，无法再添加预注册" };

            var registrations = LoadPreRegistrations();
            registrations[email] = version;
            SavePreRegistrations(registrations);
            return new { success = true };
        }
        catch (Exception ex)
        {
            return new { success = false, message = ex.Message };
        }
    }

    /// <summary>
    /// 删除预注册记录
    /// </summary>
    [HttpDelete("admin/preregistrations")]
    public object AdminDeletePreRegistration([FromBody] System.Text.Json.JsonElement request, [FromHeader(Name = "X-Admin-Token")] string? adminToken = null)
    {
        if (!IsAdminAuthorized(adminToken))
            return new { success = false, message = "未授权" };

        try
        {
            var email = request.TryGetProperty("email", out var e) ? e.GetString()?.ToLowerInvariant() : null;
            if (string.IsNullOrEmpty(email))
                return new { success = false, message = "email 不能为空" };

            var registrations = LoadPreRegistrations();
            if (!registrations.Remove(email))
                return new { success = false, message = "未找到该预注册记录" };

            SavePreRegistrations(registrations);
            return new { success = true };
        }
        catch (Exception ex)
        {
            return new { success = false, message = ex.Message };
        }
    }

    // ==============================
    // 私有辅助方法
    // ==============================

    // internal：供同程序集的 BattlePass 管理端复用同一 admin 鉴权域（X-Admin-Token / Portal SSO）。
    internal static bool IsAdminAuthorized(string? token) =>
        !string.IsNullOrEmpty(token) && AdminTokens.ContainsKey(token);

    private Dictionary<string, string> LoadPreRegistrations()
    {
        try
        {
            if (!File.Exists(PreRegisteredFilePath))
                return new Dictionary<string, string>();

            var content = File.ReadAllText(PreRegisteredFilePath);
            using var doc = JsonDocument.Parse(content);
            var result = new Dictionary<string, string>();
            if (doc.RootElement.TryGetProperty("registrations", out var regs))
            {
                foreach (var prop in regs.EnumerateObject())
                    result[prop.Name] = prop.Value.GetString()!;
            }
            return result;
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    private void SavePreRegistrations(Dictionary<string, string> data)
    {
        try
        {
            var dir = Path.GetDirectoryName(PreRegisteredFilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(new { registrations = data }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(PreRegisteredFilePath, json);
        }
        catch (Exception ex)
        {
            logger.Error($"[WebRegister] Failed to save pre_registered.json: {ex.Message}", ex);
        }
    }

    private List<string> LoadAllowedVersions()
    {
        try
        {
            if (!File.Exists(AdminConfigFilePath))
                return new List<string>();

            var content = File.ReadAllText(AdminConfigFilePath);
            using var doc = JsonDocument.Parse(content);
            var result = new List<string>();
            if (doc.RootElement.TryGetProperty("allowedVersions", out var arr))
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var v = item.GetString();
                    if (!string.IsNullOrEmpty(v)) result.Add(v);
                }
            }
            return result;
        }
        catch
        {
            return new List<string>();
        }
    }

    private void SaveAllowedVersions(List<string> versions)
    {
        try
        {
            var dir = Path.GetDirectoryName(AdminConfigFilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(new { allowedVersions = versions }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(AdminConfigFilePath, json);
        }
        catch (Exception ex)
        {
            logger.Error($"[WebRegister] Failed to save admin_config.json: {ex.Message}", ex);
        }
    }

    // ==================== N3：账户名实时校验 ====================

    /// <summary>
    /// 实时检查用户名是否可用（注册页输入防抖调用；提交时后端仍做最终查重兜底竞态）。
    /// </summary>
    [HttpGet("check-username")]
    public object CheckUsername([FromQuery] string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return new { success = false, available = false, message = "用户名不能为空" };
        }

        var taken = saveServer.GetProfiles().Values.Any(p => p.ProfileInfo?.Username == username);
        return new { success = true, available = !taken, message = taken ? "该账户名已被占用" : "该账户名可用" };
    }

    // ==================== N2：注册激活码（用户端） ====================

    /// <summary>
    /// 校验激活码有效性（不消耗）；有效时返回锁定版本，注册页据此锁定版本选择。
    /// </summary>
    [HttpPost("check-activation-code")]
    public object CheckActivationCode([FromBody] System.Text.Json.JsonElement request)
    {
        var code = request.TryGetProperty("code", out var codeProp) ? codeProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(code))
        {
            return new { success = false, valid = false, message = "激活码不能为空" };
        }

        var (valid, edition, message) = activationCodeService.ValidateCode(code);
        return new { success = true, valid, edition, message };
    }

    // ==================== N1：管理员账号管理（搜索 + 删除） ====================

    /// <summary>
    /// 按用户名/邮箱/profileId 模糊搜索账号。
    /// </summary>
    [HttpGet("admin/accounts")]
    public object AdminSearchAccounts([FromQuery] string? query, [FromHeader(Name = "X-Admin-Token")] string? adminToken = null)
    {
        if (!IsAdminAuthorized(adminToken))
        {
            return new { success = false, message = "未授权" };
        }

        var q = query?.Trim() ?? string.Empty;
        var results = new List<object>();
        foreach (var (sessionId, profile) in saveServer.GetProfiles())
        {
            var username = profile.ProfileInfo?.Username ?? string.Empty;
            var email = GetEmailByUsername(username) ?? string.Empty;
            var idString = sessionId.ToString();

            if (
                q.Length > 0
                && !username.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !email.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !idString.Contains(q, StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            var lastLogin = lastLoginService.Get(sessionId);
            results.Add(
                new
                {
                    profileId = idString,
                    username,
                    email,
                    edition = profile.ProfileInfo?.Edition,
                    lastLogin = lastLogin.HasValue ? DateTimeOffset.FromUnixTimeSeconds(lastLogin.Value).UtcDateTime : (DateTime?)null,
                }
            );
        }

        return new { success = true, accounts = results };
    }

    /// <summary>
    /// 删除账号：存档文件真删除（force 绕过软重置）+ 释放邮箱记录 + 移除映射，使该邮箱可重新注册。
    /// </summary>
    [HttpDelete("admin/accounts/{profileId}")]
    public object AdminDeleteAccount(string profileId, [FromHeader(Name = "X-Admin-Token")] string? adminToken = null)
    {
        if (!IsAdminAuthorized(adminToken))
        {
            return new { success = false, message = "未授权" };
        }

        if (!MongoId.IsValidMongoId(profileId))
        {
            return new { success = false, message = "profileId 无效" };
        }

        var sessionId = new MongoId(profileId);
        if (!saveServer.GetProfiles().TryGetValue(sessionId, out var profile))
        {
            return new { success = false, message = "账号不存在" };
        }

        var username = profile.ProfileInfo?.Username ?? string.Empty;
        var email = GetEmailByUsername(username);

        // 1. 删除存档（内存 + 两种命名的磁盘文件；force 确保软重置开启时也是真删除）
        saveServer.RemoveProfile(sessionId, force: true);

        // 2. 释放邮箱：从已注册列表移除 + 删除用户名映射，该邮箱即可重新注册
        if (!string.IsNullOrEmpty(email))
        {
            RemoveRegisteredEmail(email);
        }

        if (!string.IsNullOrEmpty(username))
        {
            RemoveEmailMapping(username);
        }

        // 3. 清理登录时间侧存储
        lastLoginService.Remove(sessionId);

        logger.Warning($"[WebRegister] 管理员删除账号: {username} ({email ?? "无邮箱"}) profileId={profileId}");
        return new { success = true, message = $"账号 {username} 已删除，邮箱已释放" };
    }

    // ==================== N2：注册激活码（管理端） ====================

    [HttpGet("admin/activation-codes")]
    public object AdminListActivationCodes([FromHeader(Name = "X-Admin-Token")] string? adminToken = null)
    {
        if (!IsAdminAuthorized(adminToken))
        {
            return new { success = false, message = "未授权" };
        }

        return new { success = true, codes = activationCodeService.ListCodes() };
    }

    [HttpPost("admin/activation-codes")]
    public object AdminCreateActivationCodes(
        [FromBody] System.Text.Json.JsonElement request,
        [FromHeader(Name = "X-Admin-Token")] string? adminToken = null
    )
    {
        if (!IsAdminAuthorized(adminToken))
        {
            return new { success = false, message = "未授权" };
        }

        var edition = request.TryGetProperty("edition", out var e) ? e.GetString() : null;
        if (string.IsNullOrWhiteSpace(edition))
        {
            return new { success = false, message = "必须指定绑定版本" };
        }

        var count = request.TryGetProperty("count", out var c) && c.TryGetInt32(out var n) ? n : 1;
        var note = request.TryGetProperty("note", out var noteProp) ? noteProp.GetString() : null;
        DateTime? expiresAt = null;
        if (request.TryGetProperty("expiresAt", out var exp) && exp.ValueKind == JsonValueKind.String && DateTime.TryParse(exp.GetString(), out var parsed))
        {
            expiresAt = parsed.ToUniversalTime();
        }

        var created = activationCodeService.CreateCodes(edition, count, note, expiresAt);
        return new { success = true, codes = created };
    }

    [HttpDelete("admin/activation-codes/{code}")]
    public object AdminRevokeActivationCode(string code, [FromHeader(Name = "X-Admin-Token")] string? adminToken = null)
    {
        if (!IsAdminAuthorized(adminToken))
        {
            return new { success = false, message = "未授权" };
        }

        return activationCodeService.RevokeCode(code)
            ? new { success = true, message = "激活码已作废" }
            : new { success = false, message = "激活码不存在或已使用/已作废" };
    }

    [HttpGet("admin/activation-codes/logs")]
    public object AdminGetActivationLogs([FromHeader(Name = "X-Admin-Token")] string? adminToken = null)
    {
        if (!IsAdminAuthorized(adminToken))
        {
            return new { success = false, message = "未授权" };
        }

        return new
        {
            success = true,
            retentionDays = activationCodeService.RetentionDays,
            logs = activationCodeService.GetLogs(),
        };
    }

    [HttpGet("admin/activation-codes/logs/export")]
    public IActionResult AdminExportActivationLogs([FromHeader(Name = "X-Admin-Token")] string? adminToken = null)
    {
        if (!IsAdminAuthorized(adminToken))
        {
            return new UnauthorizedResult();
        }

        var csv = activationCodeService.ExportLogsCsv();
        return new FileContentResult(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv")
        {
            FileDownloadName = $"activation-log-{DateTime.UtcNow:yyyyMMddHHmmss}.csv",
        };
    }

    [HttpDelete("admin/activation-codes/logs")]
    public object AdminClearActivationLogs(
        [FromQuery] string? before,
        [FromHeader(Name = "X-Admin-Token")] string? adminToken = null
    )
    {
        if (!IsAdminAuthorized(adminToken))
        {
            return new { success = false, message = "未授权" };
        }

        DateTime? cutoff = DateTime.TryParse(before, out var parsed) ? parsed.ToUniversalTime() : null;
        var removed = activationCodeService.ClearLogs(cutoff);
        return new { success = true, message = $"已删除 {removed} 条日志" };
    }

    [HttpPost("admin/activation-codes/log-retention")]
    public object AdminSetActivationLogRetention(
        [FromBody] System.Text.Json.JsonElement request,
        [FromHeader(Name = "X-Admin-Token")] string? adminToken = null
    )
    {
        if (!IsAdminAuthorized(adminToken))
        {
            return new { success = false, message = "未授权" };
        }

        var days = request.TryGetProperty("days", out var d) && d.TryGetInt32(out var n) ? n : 0;
        activationCodeService.RetentionDays = days;
        return new { success = true, retentionDays = activationCodeService.RetentionDays, message = days <= 0 ? "日志将不限时保存" : $"日志保留 {days} 天" };
    }
}


