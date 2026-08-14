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

    /// <summary>使用 WebRegister 自有配置解析管理员通知邮箱并发送，不向业务模块暴露地址或 SMTP 凭据。</summary>
    bool TrySendAdminNotification(string subject, string htmlBody, string contextId);
}

[Injectable(InjectionType.Singleton)]
public sealed class WebRegisterMailService(ISptLogger<WebRegisterMailService> logger) : IWebRegisterMailService
{
    private const int MaxSendAttempts = 3;

    public bool TrySendBackground(IEnumerable<string> recipients, string subject, string htmlBody, string contextId)
    {
        try
        {
            var config = WebRegisterModConfig.Load();
            return TryQueue(config.SmtpConfig, recipients, subject, htmlBody, contextId);
        }
        catch (Exception ex)
        {
            logger.Warning($"[WebRegister] 邮件准备失败 (context={contextId}): {ex.Message}");
            return false;
        }
    }

    public bool TrySendAdminNotification(string subject, string htmlBody, string contextId)
    {
        try
        {
            var config = WebRegisterModConfig.Load();
            var recipients = ResolveAdminRecipients(config);
            if (recipients.Count == 0)
            {
                logger.Warning($"[WebRegister] 管理员通知跳过：未配置有效管理员邮箱 (context={contextId})");
                return false;
            }

            return TryQueue(config.SmtpConfig, recipients, subject, htmlBody, contextId);
        }
        catch (Exception ex)
        {
            logger.Warning($"[WebRegister] 管理员通知准备失败 (context={contextId}): {ex.Message}");
            return false;
        }
    }

    private bool TryQueue(
        WebRegisterSmtpConfig? config,
        IEnumerable<string> recipients,
        string subject,
        string htmlBody,
        string contextId)
    {
        if (config is null
            || string.IsNullOrWhiteSpace(config.Server)
            || string.IsNullOrWhiteSpace(config.SenderEmail))
        {
            logger.Warning($"[WebRegister] 邮件跳过：SMTP 未配置 (context={contextId})");
            return false;
        }

        var validRecipients = NormalizeAddresses(recipients);
        if (validRecipients.Count == 0)
        {
            logger.Warning($"[WebRegister] 邮件跳过：无有效收件人 (context={contextId})");
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

        _ = Task.Run(() => SendWithRetryAsync(snapshot, validRecipients, subject, htmlBody, contextId));
        logger.Info($"[WebRegister] 邮件已进入发送队列，收件人 {validRecipients.Count} 位 (context={contextId})");
        return true;
    }

    private async Task SendWithRetryAsync(
        SmtpSnapshot smtp,
        IReadOnlyCollection<string> recipients,
        string subject,
        string body,
        string contextId)
    {
        for (var attempt = 1; attempt <= MaxSendAttempts; attempt++)
        {
            if (TrySend(smtp, recipients, subject, body, out var error))
            {
                logger.Info($"[WebRegister] 邮件发送成功，收件人 {recipients.Count} 位 (context={contextId}, attempt={attempt})");
                return;
            }

            if (attempt == MaxSendAttempts)
            {
                logger.Warning($"[WebRegister] 邮件发送失败，已重试 {MaxSendAttempts} 次 (context={contextId}): {error}");
                return;
            }

            logger.Warning($"[WebRegister] 邮件发送失败，准备重试 (context={contextId}, attempt={attempt}): {error}");
            await Task.Delay(TimeSpan.FromSeconds(attempt));
        }
    }

    private static bool TrySend(
        SmtpSnapshot smtp,
        IReadOnlyCollection<string> recipients,
        string subject,
        string body,
        out string? error)
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
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static List<string> ResolveAdminRecipients(WebRegisterModConfig config)
    {
        var configured = NormalizeAddresses(config.WebRegisterConfig?.AdminNotificationEmails ?? []);
        if (configured.Count > 0) return configured;

        var fallback = NormalizeAddresses([
            config.SmtpConfig?.SenderEmail ?? "",
            config.SmtpConfig?.Username ?? "",
        ]);
        return fallback.Take(1).ToList();
    }

    private static List<string> NormalizeAddresses(IEnumerable<string> addresses) => addresses
        .Select(address => address?.Trim())
        .Where(address => !string.IsNullOrWhiteSpace(address)
            && address.IndexOf('@') > 0
            && address.LastIndexOf('@') < address.Length - 1
            && MailboxAddress.TryParse(address, out _))
        .Select(address => address!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

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
