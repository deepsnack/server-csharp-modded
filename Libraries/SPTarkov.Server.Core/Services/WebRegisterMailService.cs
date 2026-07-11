using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Utils;

namespace SPTarkov.Server.Core.Services;

/// <summary>WebRegister 对其它模块暴露的邮件发送能力；不暴露 SMTP 凭据。</summary>
public interface IWebRegisterMailService
{
    /// <summary>校验地址并在后台发送 HTML 邮件。成功进入发送队列返回 true。</summary>
    bool TrySendBackground(IEnumerable<string> recipients, string subject, string htmlBody, string contextId);
}

[Injectable(InjectionType.Singleton)]
public sealed class WebRegisterMailService(ISptLogger<WebRegisterMailService> logger) : IWebRegisterMailService
{
    public bool TrySendBackground(IEnumerable<string> recipients, string subject, string htmlBody, string contextId)
    {
        try
        {
            var config = WebRegisterModConfig.Load().SmtpConfig;
            if (config is null
                || string.IsNullOrWhiteSpace(config.Server)
                || string.IsNullOrWhiteSpace(config.SenderEmail))
            {
                logger.Debug($"[WebRegister] 邮件跳过：SMTP 未配置 (context={contextId})");
                return false;
            }

            var validRecipients = recipients
                .Select(address => address?.Trim())
                .Where(address => !string.IsNullOrWhiteSpace(address) && MailboxAddress.TryParse(address, out _))
                .Select(address => address!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (validRecipients.Count == 0)
            {
                logger.Debug($"[WebRegister] 邮件跳过：无有效收件人 (context={contextId})");
                return false;
            }

            var snapshot = new SmtpSnapshot(
                config.Server,
                config.Port,
                config.UseSsl,
                config.Username,
                config.Password,
                config.SenderEmail,
                string.IsNullOrWhiteSpace(config.SenderName) ? "SPT 通知" : config.SenderName!,
                config.TimeoutMs > 0 ? config.TimeoutMs : 30000);

            _ = Task.Run(() => Send(snapshot, validRecipients, subject, htmlBody, contextId));
            return true;
        }
        catch (Exception ex)
        {
            logger.Warning($"[WebRegister] 邮件准备失败 (context={contextId}): {ex.Message}");
            return false;
        }
    }

    private void Send(SmtpSnapshot smtp, IReadOnlyCollection<string> recipients, string subject, string body, string contextId)
    {
        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(smtp.SenderName, smtp.SenderEmail));
            foreach (var recipient in recipients)
            {
                message.To.Add(MailboxAddress.Parse(recipient));
            }

            message.Subject = subject;
            message.Body = new TextPart("html") { Text = body };

            var options = smtp.Port == 465
                ? SecureSocketOptions.SslOnConnect
                : smtp.UseSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None;
            using var client = new SmtpClient { Timeout = smtp.TimeoutMs };
            client.Connect(smtp.Server, smtp.Port, options);
            if (!string.IsNullOrWhiteSpace(smtp.Username))
            {
                client.Authenticate(smtp.Username, smtp.Password);
            }
            client.Send(message);
            client.Disconnect(true);
            logger.Info($"[WebRegister] 邮件发送成功，收件人 {recipients.Count} 位 (context={contextId})");
        }
        catch (Exception ex)
        {
            logger.Warning($"[WebRegister] 邮件发送失败 (context={contextId}): {ex.Message}");
        }
    }

    private sealed record SmtpSnapshot(
        string Server,
        int Port,
        bool UseSsl,
        string Username,
        string Password,
        string SenderEmail,
        string SenderName,
        int TimeoutMs);
}
