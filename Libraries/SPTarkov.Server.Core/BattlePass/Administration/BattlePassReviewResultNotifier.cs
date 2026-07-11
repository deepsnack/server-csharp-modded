using System.Net;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;

namespace SPTarkov.Server.Core.BattlePass.Administration;

/// <summary>发送通行证审核邮件：协管提交提醒管理员，审核结果提醒协管。</summary>
[Injectable(InjectionType.Singleton)]
public sealed class BattlePassReviewResultNotifier(
    IWebRegisterAccountEmailService accountEmailService,
    IWebRegisterMailService mailService,
    ISptLogger<BattlePassReviewResultNotifier> logger)
{
    public bool NotifyApproved(BpChangeRequest change) => Notify(change, approved: true);

    public bool NotifyRejected(BpChangeRequest change) => Notify(change, approved: false);

    public bool NotifySubmitted(BpChangeRequest change)
    {
        var recipients = ResolveAdminRecipients();
        if (recipients.Count == 0)
        {
            logger.Warning($"[BattlePass] 审核提交通知跳过：未配置管理员提醒邮箱 (change={change.Id})");
            return false;
        }

        var subject = $"[SPT 通行证] 待审核：{change.TargetDisplayName}";
        var body = $"""
            <html><body style="font-family:sans-serif;color:#20242a">
            <h2>通行证协管提交待审核</h2>
            <p>协管 <strong>{Html(change.Actor.DisplayName)}</strong> 提交了一项变更，等待管理员审核。</p>
            <ul>
              <li>模块：{Html(change.Module)}</li>
              <li>目标：{Html(change.TargetDisplayName)}</li>
              <li>摘要：{Html(change.Summary)}</li>
              <li>变更编号：{Html(change.Id)}</li>
            </ul>
            <p style="color:#68717d">请打开通行证管理页的审核中心处理。</p>
            </body></html>
            """;
        return mailService.TrySendBackground(recipients, subject, body, $"battlepass-review:{change.Id}:submitted");
    }

    public int NotifyApprovedBatch(IEnumerable<BpChangeRequest> changes)
    {
        var grouped = changes
            .Where(change => change is not null)
            .Select(change =>
            {
                var profileId = CollaboratorProfileId(change);
                return new
                {
                    Change = change,
                    ProfileId = profileId,
                    Email = accountEmailService.ResolveByProfileId(profileId),
                };
            })
            .ToList();

        foreach (var missing in grouped.Where(item => string.IsNullOrWhiteSpace(item.Email)))
        {
            logger.Warning(
                $"[BattlePass] 批量审核结果通知跳过：协管无注册邮箱 (change={missing.Change.Id}, profileId={missing.ProfileId})"
            );
        }

        var queued = 0;
        foreach (var group in grouped
            .Where(item => !string.IsNullOrWhiteSpace(item.Email))
            .GroupBy(item => item.Email!, StringComparer.OrdinalIgnoreCase))
        {
            var approvedChanges = group.Select(item => item.Change).ToList();
            if (approvedChanges.Count == 0)
            {
                continue;
            }

            var actorName = approvedChanges.First().Actor.DisplayName;
            var subject = $"[SPT 通行证] 批量审核通过：{approvedChanges.Count} 项变更";
            var rows = string.Join(
                Environment.NewLine,
                approvedChanges.Select(change =>
                    $"<li><strong>{Html(change.TargetDisplayName)}</strong>：{Html(change.Summary)}<br><span style=\"color:#68717d\">模块：{Html(change.Module)} · 编号：{Html(change.Id)}</span></li>"
                )
            );
            var body = $"""
                <html><body style="font-family:sans-serif;color:#20242a">
                <h2>通行证协管审核结果</h2>
                <p>你好，{Html(actorName)}：</p>
                <p>管理员已批量批准你的 {approvedChanges.Count} 项提交，改动已经即时生效。</p>
                <ul>{rows}</ul>
                </body></html>
                """;
            if (mailService.TrySendBackground([group.Key], subject, body, $"battlepass-review:batch-approved:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:{group.Key}"))
            {
                queued++;
            }
        }

        return queued;
    }

    private bool Notify(BpChangeRequest change, bool approved)
    {
        var profileId = CollaboratorProfileId(change);
        var email = accountEmailService.ResolveByProfileId(profileId);
        if (string.IsNullOrWhiteSpace(email))
        {
            logger.Warning($"[BattlePass] 审核结果通知跳过：协管无注册邮箱 (change={change.Id}, profileId={profileId})");
            return false;
        }

        var outcome = approved ? "审核通过" : "已被驳回";
        var subject = $"[SPT 通行证] {outcome}：{change.TargetDisplayName}";
        var reason = approved
            ? "管理员已批准该变更，改动已经即时生效。"
            : $"驳回理由：{WebUtility.HtmlEncode(change.ReviewReason ?? "未填写")}。";
        var body = $"""
            <html><body style="font-family:sans-serif;color:#20242a">
            <h2>通行证协管审核结果</h2>
            <p>你好，{WebUtility.HtmlEncode(change.Actor.DisplayName)}：</p>
            <p>你的提交 <strong>{WebUtility.HtmlEncode(change.Summary)}</strong> {outcome}。</p>
            <p>{reason}</p>
            <p style="color:#68717d">变更编号：{WebUtility.HtmlEncode(change.Id)} · 模块：{WebUtility.HtmlEncode(change.Module)}</p>
            </body></html>
            """;
        return mailService.TrySendBackground([email], subject, body, $"battlepass-review:{change.Id}:{(approved ? "approved" : "rejected")}");
    }

    private static string CollaboratorProfileId(BpChangeRequest change) => change.Actor.ProfileId ?? change.Actor.ActorId;

    private static string Html(string? value) => WebUtility.HtmlEncode(value ?? "");

    private static List<string> ResolveAdminRecipients()
    {
        var config = WebRegisterModConfig.Load();
        var recipients = config.WebRegisterConfig?.AdminNotificationEmails ?? [];
        if (recipients.Count == 0 && !string.IsNullOrWhiteSpace(config.SmtpConfig?.SenderEmail))
        {
            recipients = [config.SmtpConfig.SenderEmail];
        }

        return recipients
            .Select(email => email?.Trim())
            .Where(email => !string.IsNullOrWhiteSpace(email))
            .Select(email => email!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
