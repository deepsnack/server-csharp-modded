using System.Net;
using System.Net.Mail;
using System.Text;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Logging;
using SPTarkov.Server.Core.Models.Utils;

namespace SPTarkov.Server.Core.Services;

/// <summary>
/// SMTP 邮件发送服务
/// </summary>
[Injectable(InjectionType.Singleton)]
public class EmailService
{
    private readonly ISptLogger<EmailService> _logger;
    private readonly CoreConfig _coreConfig;

    public EmailService(ISptLogger<EmailService> logger, CoreConfig coreConfig)
    {
        _logger = logger;
        _coreConfig = coreConfig;
    }

    /// <summary>
    /// 发送验证码邮件
    /// </summary>
    /// <param name="toEmail">收件人邮箱</param>
    /// <param name="verificationCode">验证码</param>
    /// <returns>发送是否成功</returns>
    public bool SendVerificationCodeEmail(string toEmail, string verificationCode)
    {
        try
        {
            // 检查 SMTP 配置
            if (_coreConfig.SmtpConfig == null)
            {
                _logger.Error("SMTP 配置未设置，无法发送邮件");
                return false;
            }

            // 构建邮件内容
            var subject = "验证码";
            var body = BuildVerificationEmailBody(verificationCode);

            // 发送邮件
            return SendEmail(toEmail, subject, body);
        }
        catch (Exception ex)
        {
            _logger.Error($"发送验证码邮件失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 发送邮件
    /// </summary>
    /// <param name="toEmail">收件人邮箱</param>
    /// <param name="subject">邮件主题</param>
    /// <param name="body">邮件内容</param>
    /// <returns>发送是否成功</returns>
    public bool SendEmail(string toEmail, string subject, string body)
    {
        try
        {
            // 检查 SMTP 配置
            if (_coreConfig.SmtpConfig == null)
            {
                _logger.Error("SMTP 配置未设置，无法发送邮件");
                return false;
            }

            var smtpConfig = _coreConfig.SmtpConfig;

            // 创建邮件消息
            using var mailMessage = new MailMessage
            {
                From = new MailAddress(smtpConfig.SenderEmail),
                Subject = subject,
                Body = body,
                BodyEncoding = Encoding.UTF8,
                SubjectEncoding = Encoding.UTF8,
                IsBodyHtml = true
            };

            mailMessage.To.Add(toEmail);

            // 创建 SMTP 客户端
            using var smtpClient = new SmtpClient
            {
                Host = smtpConfig.Server,
                Port = smtpConfig.Port,
                EnableSsl = smtpConfig.UseSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false,
                Credentials = new NetworkCredential(smtpConfig.Username, smtpConfig.Password),
                Timeout = 30000 // 30秒超时
            };

            // 发送邮件
            _logger.Debug($"正在发送邮件到 {toEmail}...");
            smtpClient.Send(mailMessage);

            _logger.Info($"邮件发送成功: {toEmail}");
            return true;
        }
        catch (SmtpException ex)
        {
            _logger.Error($"SMTP 邮件发送失败: {ex.Message}");
            if (ex.InnerException != null)
            {
                _logger.Error($"内部异常: {ex.InnerException.Message}");
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.Error($"邮件发送失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 构建验证码邮件内容
    /// </summary>
    /// <param name="verificationCode">验证码</param>
    /// <returns>邮件内容（HTML格式）</returns>
    private string BuildVerificationEmailBody(string verificationCode)
    {
        var emailBody = new StringBuilder();

        emailBody.AppendLine("<!DOCTYPE html>");
        emailBody.AppendLine("<html>");
        emailBody.AppendLine("<head>");
        emailBody.AppendLine("<meta charset=\"UTF-8\">");
        emailBody.AppendLine("<title>验证码</title>");
        emailBody.AppendLine("<style>");
        emailBody.AppendLine("body { font-family: Arial, sans-serif; line-height: 1.6; color: #333; }");
        emailBody.AppendLine(".container { max-width: 600px; margin: 0 auto; padding: 20px; }");
        emailBody.AppendLine(".header { background-color: #4CAF50; color: white; padding: 20px; text-align: center; }");
        emailBody.AppendLine(".content { padding: 20px; background-color: #f9f9f9; }");
        emailBody.AppendLine(".code { font-size: 32px; font-weight: bold; color: #4CAF50; text-align: center; margin: 20px 0; letter-spacing: 5px; }");
        emailBody.AppendLine(".footer { text-align: center; padding: 20px; color: #666; font-size: 12px; }");
        emailBody.AppendLine("</style>");
        emailBody.AppendLine("</head>");
        emailBody.AppendLine("<body>");
        emailBody.AppendLine("<div class=\"container\">");
        emailBody.AppendLine("<div class=\"header\">");
        emailBody.AppendLine("<h2>验证码</h2>");
        emailBody.AppendLine("</div>");
        emailBody.AppendLine("<div class=\"content\">");
        emailBody.AppendLine("<p>您好，</p>");
        emailBody.AppendLine("<p>您的验证码是：</p>");
        emailBody.AppendLine($"<div class=\"code\">{verificationCode}</div>");
        emailBody.AppendLine("<p>验证码有效期为 10 分钟，请尽快使用。</p>");
        emailBody.AppendLine("<p>如果这不是您本人的操作，请忽略此邮件。</p>");
        emailBody.AppendLine("</div>");
        emailBody.AppendLine("<div class=\"footer\">");
        emailBody.AppendLine("<p>此邮件由系统自动发送，请勿回复。</p>");
        emailBody.AppendLine("</div>");
        emailBody.AppendLine("</div>");
        emailBody.AppendLine("</body>");
        emailBody.AppendLine("</html>");

        return emailBody.ToString();
    }

    /// <summary>
    /// 测试 SMTP 连接
    /// </summary>
    /// <returns>连接是否成功</returns>
    public bool TestSmtpConnection()
    {
        try
        {
            if (_coreConfig.SmtpConfig == null)
            {
                _logger.Error("SMTP 配置未设置");
                return false;
            }

            var smtpConfig = _coreConfig.SmtpConfig;

            using var smtpClient = new SmtpClient
            {
                Host = smtpConfig.Server,
                Port = smtpConfig.Port,
                EnableSsl = smtpConfig.UseSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false,
                Credentials = new NetworkCredential(smtpConfig.Username, smtpConfig.Password),
                Timeout = 10000 // 10秒超时
            };

            _logger.Debug($"正在测试 SMTP 连接: {smtpConfig.Server}:{smtpConfig.Port}");

            // 尝试连接
            smtpClient.Send(new MailMessage
            {
                From = new MailAddress(smtpConfig.SenderEmail),
                To = { smtpConfig.SenderEmail },
                Subject = "SMTP 连接测试",
                Body = "这是一封测试邮件，用于验证 SMTP 配置是否正确。",
                IsBodyHtml = false
            });

            _logger.Info("SMTP 连接测试成功");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"SMTP 连接测试失败: {ex.Message}");
            return false;
        }
    }
}
