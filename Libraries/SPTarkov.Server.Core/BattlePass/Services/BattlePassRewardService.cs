using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>
///     把通行证奖励经游戏内邮件发给玩家，发件人为「通行证管家」联系人。
///     物品父子重整由 MailSendService 内部完成，这里只需提供扁平 Item 列表（Id/Template/StackObjectsCount）。
/// </summary>
[Injectable]
public class BattlePassRewardService(MailSendService mailSendService, ISptLogger<BattlePassRewardService> logger)
{
    private const long ThirtyDaysSeconds = 30L * 24 * 3600;

    /// <summary>发放一组奖励。<paramref name="profileId"/> 即 sessionId。</summary>
    public void Deliver(string profileId, IEnumerable<BpReward> rewards, string message)
    {
        // profileId 必须是合法 24 位 hex 的 MongoId，否则 new MongoId(profileId) 会抛 FormatException。
        if (!MongoId.IsValidMongoId(profileId))
        {
            logger.Error($"[SPT-BattlePass] 发放奖励失败：profileId 非法 profile={profileId}");
            return;
        }

        var items = new List<Item>();
        foreach (var r in rewards)
        {
            var tpl = r.Tpl?.Trim() ?? "";
            if (tpl.Length == 0 || r.Count <= 0)
            {
                continue;
            }

            // 校验 tpl 为合法 MongoId。非法（如 24 位含非 hex 字符、长度不符）只跳过该项并点名，
            // 不让一个坏 tpl 用 FormatException("ObjectId contains invalid hex characters") 拖垮整封邮件。
            if (!MongoId.IsValidMongoId(tpl))
            {
                logger.Error($"[SPT-BattlePass] 跳过非法奖励 tpl=\"{tpl}\"（profile={profileId}），请检查通行证奖励配置");
                continue;
            }

            items.Add(
                new Item
                {
                    Id = new MongoId(),
                    Template = new MongoId(tpl),
                    Upd = new Upd { StackObjectsCount = r.Count },
                }
            );
        }

        if (items.Count == 0)
        {
            return;
        }

        try
        {
            mailSendService.SendUserMessageToPlayer(
                new MongoId(profileId),
                BattlePassChatBot.Sender,
                message,
                items,
                ThirtyDaysSeconds
            );
        }
        catch (Exception ex)
        {
            logger.Error($"[SPT-BattlePass] 发放奖励失败 profile={profileId}: {ex.Message}");
        }
    }
}
