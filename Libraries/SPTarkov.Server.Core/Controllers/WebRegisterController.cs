using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Launcher;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Spt.Config;
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
    ConfigServer configServer
)
{
    // 获取SMTP配置
    private readonly CoreConfig _coreConfig = configServer.GetConfig<CoreConfig>();

    // 验证码存储（邮箱 -> 验证码）
    private static readonly Dictionary<string, string> VerificationCodes = new();

    // 验证码过期时间（邮箱 -> 过期时间）
    private static readonly Dictionary<string, DateTime> VerificationCodeExpiry = new();

    // 验证码有效期（分钟）
    private const int VerificationCodeExpiryMinutes = 5;

    // 已注册邮箱文件路径
    private static string RegisteredEmailsFilePath => Path.Combine(Directory.GetCurrentDirectory(), "SPT_Data", "database", "registered_emails.json");

    // 邮箱用户名映射文件路径
    private static string EmailMappingFilePath => Path.Combine(Directory.GetCurrentDirectory(), "SPT_Data", "database", "email_mapping.json");

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
            
            var versions = new List<string>();
            
            // profiles.json 的每个 key 就是一个版本
            foreach (var property in document.RootElement.EnumerateObject())
            {
                versions.Add(property.Name);
            }

            return new
            {
                success = true,
                versions = versions
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
    public object SendVerificationCode([FromBody] dynamic request)
    {
        try
        {
            string? email = request.email;

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
            var smtpConfig = _coreConfig.SmtpConfig;

            // 发送邮件
            if (smtpConfig != null && !string.IsNullOrEmpty(smtpConfig.Server))
            {
                SendEmailWithSmtp(smtpConfig, email, verificationCode);
                return new
                {
                    success = true,
                    message = "验证码已发送到您的邮箱"
                };
            }
            else
            {
                // 如果没有配置SMTP，则在控制台输出（仅用于测试）
                Console.WriteLine($"【测试用】验证码已发送到 {email}: {verificationCode}");
                return new
                {
                    success = true,
                    message = "验证码已发送（测试模式）",
                    // 测试模式下返回验证码
                    code = verificationCode
                };
            }
        }
        catch (Exception ex)
        {
            return new { success = false, message = $"发送验证码失败: {ex.Message}" };
        }
    }

    /// <summary>
    /// 使用SMTP发送邮件
    /// </summary>
    private void SendEmailWithSmtp(SmtpConfig smtpConfig, string toEmail, string verificationCode)
    {
        try
        {
            using var client = new SmtpClient(smtpConfig.Server, smtpConfig.Port)
            {
                EnableSsl = smtpConfig.UseSsl,
                Credentials = new NetworkCredential(smtpConfig.Username, smtpConfig.Password),
                Timeout = 10000
            };

            var message = new MailMessage
            {
                From = new MailAddress(smtpConfig.SenderEmail, "SPT注册验证"),
                Subject = "SPT 注册验证码",
                Body = $@"<html>
<body style=""font-family: Arial, sans-serif; padding: 20px;"">
    <div style=""max-width: 600px; margin: 0 auto; padding: 20px; border: 1px solid #ddd; border-radius: 8px;"">
        <h2 style=""color: #333;"">SPT 注册验证码</h2>
        <p style=""font-size: 16px; color: #555;"">您好，</p>
        <p style=""font-size: 16px; color: #555;"">您的注册验证码是：</p>
        <div style=""font-size: 32px; font-weight: bold; color: #667eea; padding: 15px; background: #f5f5f5; border-radius: 8px; text-align: center; letter-spacing: 5px; margin: 20px 0;"">
            {verificationCode}
        </div>
        <p style=""font-size: 14px; color: #999;"">验证码有效期为 {VerificationCodeExpiryMinutes} 分钟，请尽快完成注册。</p>
        <p style=""font-size: 14px; color: #999; margin-top: 20px;"">如果这不是您的操作，请忽略此邮件。</p>
    </div>
</body>
</html>",
                IsBodyHtml = true
            };
            message.To.Add(toEmail);

            client.Send(message);
            Console.WriteLine($"验证码已通过SMTP发送到: {toEmail}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SMTP发送失败: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 处理用户注册
    /// </summary>
    /// <param name="request">注册请求</param>
    /// <returns>注册结果</returns>
    [HttpPost("register")]
    public object Register([FromBody] WebRegisterRequest request)
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

            // 检查用户名是否已存在
            foreach (var (_, profile) in saveServer.GetProfiles())
            {
                if (request.Username == profile.ProfileInfo?.Username)
                {
                    return new WebRegisterResponse
                    {
                        Success = false,
                        Message = "用户名已存在"
                    };
                }
            }

            // 创建用户账户
            var profileId = CreateAccount(request);

            if (profileId.IsEmpty)
            {
                return new WebRegisterResponse
                {
                    Success = false,
                    Message = "创建账户失败"
                };
            }

            // 清除已使用的验证码
            VerificationCodes.Remove(request.Email);
            VerificationCodeExpiry.Remove(request.Email);

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
    private MongoId CreateAccount(WebRegisterRequest request)
    {
        try
        {
            var profileId = new MongoId();
            var scavId = new MongoId();

            // 使用 SHA256 算法对密码进行加密
            var encryptedPassword = EncryptPassword(request.Password!);

            var newProfileDetails = new Info
            {
                ProfileId = profileId,
                ScavengerId = scavId,
                Aid = hashUtil.GenerateAccountId(),
                Username = request.Username,
                Password = encryptedPassword,
                IsWiped = true,
                Edition = request.Edition
            };

            saveServer.CreateProfile(newProfileDetails);

            // 异步加载和保存配置文件
            _ = Task.Run(async () =>
            {
                await saveServer.LoadProfileAsync(profileId);
                await saveServer.SaveProfileAsync(profileId);
            });

            return profileId;
        }
        catch
        {
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
            Console.WriteLine($"添加注册邮箱失败: {ex.Message}");
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
            Console.WriteLine($"删除注册邮箱失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 从已注册列表中删除邮箱（通过用户名）
    /// </summary>
    private bool RemoveRegisteredEmailByUsername(string username)
    {
        try
        {
            // 查找该用户名对应的邮箱
            foreach (var (_, profile) in saveServer.GetProfiles())
            {
                if (profile.ProfileInfo?.Username == username)
                {
                    // 找到对应的邮箱记录，删除它
                    // 注意：这里我们无法直接获取邮箱，因为profile中不存储邮箱
                    // 所以这个方法需要通过其他方式调用
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"通过用户名删除注册邮箱失败: {ex.Message}");
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
            Console.WriteLine($"保存注册邮箱列表失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 删除已注册邮箱（当用户删除存档时调用）
    /// </summary>
    /// <param name="request">包含邮箱的请求</param>
    /// <returns>删除结果</returns>
    [HttpPost("unregister-email")]
    public object UnregisterEmail([FromBody] dynamic request)
    {
        try
        {
            string? email = request.email;

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
    public object UnregisterEmailByUsername([FromBody] dynamic request)
    {
        try
        {
            string? username = request.username;

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
            Console.WriteLine($"保存邮箱映射失败: {ex.Message}");
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
            Console.WriteLine($"添加邮箱映射失败: {ex.Message}");
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
            Console.WriteLine($"删除邮箱映射失败: {ex.Message}");
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
            // 根据sessionId查找对应的用户名和邮箱
            string? usernameToRemove = null;
            
            foreach (var (_, profile) in saveServer.GetProfiles())
            {
                if (profile.ProfileInfo?.ProfileId == sessionId)
                {
                    usernameToRemove = profile.ProfileInfo.Username;
                    break;
                }
            }

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
                Console.WriteLine($"已删除用户 {usernameToRemove} ({email}) 的邮箱记录");
            }

            return new { success = true, message = "邮箱记录已删除" };
        }
        catch (Exception ex)
        {
            return new { success = false, message = $"删除邮箱记录失败: {ex.Message}" };
        }
    }
}
