using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Services.Mod;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>注册任务目标跳过子模块使用的独立消耗券。</summary>
[Injectable(InjectionType.Singleton)]
public class QuestSkipTicketService(
    CustomItemService customItemService,
    DatabaseService databaseService,
    ISptLogger<QuestSkipTicketService> logger
)
{
    public static readonly MongoId TicketTpl = new("1b7b964393a04d11ac1d6eee");
    public const string TicketNameZh = "任务跳过券";
    private const string KeycardHandbookParent = "5c518ed586f774119a772aee";

    public void Register()
    {
        if (databaseService.GetItems().ContainsKey(TicketTpl))
        {
            return;
        }

        var result = customItemService.CreateItemFromClone(
            new NewItemFromCloneDetails
            {
                ItemTplToClone = ItemTpl.KEYCARD_TERRAGROUP_LABS_ACCESS,
                NewId = TicketTpl.ToString(),
                ParentId = BaseClasses.KEYCARD.ToString(),
                HandbookParentId = KeycardHandbookParent,
                HandbookPriceRoubles = 0,
                FleaPriceRoubles = 0,
                OverrideProperties = new TemplateItemProperties
                {
                    Name = TicketNameZh,
                    ShortName = "跳过券",
                    Description = "在通行证网页中跳过一个任务目标。使用时必须放在主仓库中，每次消耗一张。",
                    StackMaxSize = 1,
                    QuestItem = false,
                    CanSellOnRagfair = false,
                    CanRequireOnRagfair = false,
                    IsUnsaleable = true,
                    IsUnbuyable = true,
                    // ponytail: 不设 IsUngivable —— 该标志会让客户端无法从邮箱取出券，导致商店/奖励轨/任务/抽奖发放全部失效。
                    // 不可出售/不可跳蚤已由 IsUnsaleable + CanSellOnRagfair/CanRequireOnRagfair=false 覆盖；不进任何掉落/货架即不自然获得。
                },
                Locales = new Dictionary<string, LocaleDetails>
                {
                    ["ch"] = new()
                    {
                        Name = TicketNameZh,
                        ShortName = "跳过券",
                        Description = "在通行证网页中跳过一个任务目标。使用时必须放在主仓库中，每次消耗一张。",
                    },
                    ["en"] = new()
                    {
                        Name = "Quest Skip Ticket",
                        ShortName = "Skip Ticket",
                        Description = "Skips one quest objective from the Battle Pass web page. Must be in the main stash; one ticket is consumed each time.",
                    },
                },
            },
            CustomItemOwnershipMode.CoreOwned
        );

        if (result.Success != true)
        {
            throw new InvalidOperationException($"无法注册任务跳过券: {string.Join("; ", result.Errors ?? [])}");
        }

        logger.Success($"[SPT-BattlePass] 已注册{TicketNameZh}");
    }
}
