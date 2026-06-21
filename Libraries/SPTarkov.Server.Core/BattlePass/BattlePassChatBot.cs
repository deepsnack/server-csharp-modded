using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Dialogue;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Dialog;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Enums;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>
///     通行证专属联系人「通行证管家」。注册为 IDialogueChatBot 后出现在游戏内好友/联系人列表，
///     所有通行证奖励邮件以它为发件人（仿 Fika 联系人）。仿 server-mod-examples/20CustomChatBot。
/// </summary>
[Injectable]
public class BattlePassChatBot : IDialogueChatBot
{
    /// <summary>固定联系人 id，奖励发放与该 dialog 关联。</summary>
    public const string ContactId = "655000b00000b00000b00001";

    public static UserDialogInfo Sender =>
        new()
        {
            Id = ContactId,
            Aid = 1000111,
            Info = new UserDialogDetails
            {
                Nickname = "通行证管家",
                Side = "Usec",
                Level = 69,
                MemberCategory = MemberCategory.Developer,
                SelectedMemberCategory = MemberCategory.Developer,
            },
        };

    public UserDialogInfo GetChatBot()
    {
        return Sender;
    }

    public ValueTask<string> HandleMessage(MongoId sessionId, SendMessageRequest request)
    {
        // 联系人暂不处理玩家来信（领奖在网页操作）；返回空表示不处理。
        return ValueTask.FromResult(string.Empty);
    }
}
