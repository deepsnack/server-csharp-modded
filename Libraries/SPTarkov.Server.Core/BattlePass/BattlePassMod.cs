using SPTarkov.Server.Core.BattlePass.Portal;
using SPTarkov.Server.Core.BattlePass.Administration;
using SPTarkov.Server.Core.BattlePass.Patches;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>
///     战斗通行证模块入口（SPT-AccountWeb 合并包的第 5 个模块）。
///     职责：落盘默认配置 → 解压网页资源 → 启用 /battlepass 静态页别名 patch → 打印访问地址。
///     玩家/管理 API 由 MVC 自动发现（程序集已由 AccountWebMetadata 的 IModWebMetadata 注册为 ApplicationPart）。
///     认证复用包内 PasswordStore（玩家）与 WebRegisterController 的 admin token（管理员）。
/// </summary>
// Mod item templates are created during PostDB callbacks. Run after the shared loot index (+89000),
// but before item/trader control finalization (+90000), so configured mod offers exist in the initial assort.
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 89999)]
public class BattlePassMod(
    ISptLogger<BattlePassMod> logger,
    ConfigServer configServer,
    BattlePassPortalBridgeService portalBridge,
    BattlePassTraderSync traderSync,
    BattlePassRecipeSync recipeSync,
    QuestSkipTicketService questSkipTicketService,
    BattlePassService battlePassService,
    BattlePassTraderAccessService traderAccessService,
    BattlePassReviewService reviewService,
    ShopChangeHandler shopChangeHandler,
    TaskChangeHandler taskChangeHandler,
    TrackChangeHandler trackChangeHandler,
    LotteryChangeHandler lotteryChangeHandler,
    TraderChangeHandler traderChangeHandler,
    RecipeChangeHandler recipeChangeHandler,
    ItemsChangeHandler itemsChangeHandler,
    FleaChangeHandler fleaChangeHandler,
    QuestChangeHandler questChangeHandler,
    TitleChangeHandler titleChangeHandler
) : IOnLoad
{
    public Task OnLoad()
    {
        BattlePassModConfig.Load();   // 确保 SPT_Data/battlepass/config.json 存在；首次运行自动写模板
        BattlePassStore.EnsureSeeded();
        battlePassService.InitializeExistingRewardLedgers();
        traderAccessService.Register();
        questSkipTicketService.Register();

        // 注册协管审核模块处理器（管理员即时写入与协管审核放行共用同一业务入口），
        // 并恢复上次未完成的 applying 变更（重启前批准中途中断的），保证审核链路可用。
        reviewService.RegisterHandler(shopChangeHandler);
        reviewService.RegisterHandler(taskChangeHandler);
        reviewService.RegisterHandler(trackChangeHandler);
        reviewService.RegisterHandler(lotteryChangeHandler);
        reviewService.RegisterHandler(traderChangeHandler);
        reviewService.RegisterHandler(recipeChangeHandler);
        reviewService.RegisterHandler(itemsChangeHandler);
        reviewService.RegisterHandler(fleaChangeHandler);
        reviewService.RegisterHandler(questChangeHandler);
        reviewService.RegisterHandler(titleChangeHandler);
        reviewService.RecoverOnStartup();
        reviewService.PurgeExpiredAudit();
        // 页面随 Assets 工程输出到 SPT_Data/battlepass/page/，无需运行期解压；
        // 静态页 /battlepass 由 BattlePassPageController（MVC catch-all）提供。
        traderSync.Sync(resetPurchaseState: true); // 注入「通行证商人」并在服务端重启时同步刷新实际库存/限购
        recipeSync.Sync(); // 注入自定义藏身处配方（锁定配方挂通行证虚拟任务锁，经奖励轨 recipe 解锁）
        portalBridge.Start(); // Portal 兼容 sidecar：作为"通行证管理"独立卡片自注册到 SptManagerPortal，支持 SSO 免密进管理页

        try
        {
            new EndLocalRaidTrackPatch().Enable();
        }
        catch (Exception ex)
        {
            logger.Warning($"[SPT-BattlePass] EndLocalRaid 任务追踪补丁启用失败，服务端权威追踪不可用: {ex.Message}");
        }

        // 普通任务由服务端 EndLocalRaid 战后档案权威结算；客户端插件补充武器条件击杀、VisitZone/PlaceItem 与实时反馈。
        // 服务端不注入任何原生 quest，加载链路零副作用。
        logger.Success("[SPT-BattlePass] loaded; player API /battlepass/api/*, admin API /battlepass/api/admin/*");
        PrintAddress();
        return Task.CompletedTask;
    }

    private void PrintAddress()
    {
        try
        {
            var httpConfig = configServer.GetConfig<HttpConfig>();
            var host = string.IsNullOrEmpty(httpConfig.Ip) || httpConfig.Ip == "0.0.0.0" ? "127.0.0.1" : httpConfig.Ip;

            logger.Success("================ 战斗通行证 / Battle Pass ================");
            logger.Success($"  玩家页: https://{host}:{httpConfig.Port}/battlepass/index.html");
            logger.Success($"  称号页: https://{host}:{httpConfig.Port}/battlepass/titles.html");
            logger.Success($"  管理页: https://{host}:{httpConfig.Port}/battlepass/admin/index.html");
            logger.Success($"  任务页: https://{host}:{httpConfig.Port}/battlepass/admin/tasks.html");
            logger.Success($"  商人任务管理: https://{host}:{httpConfig.Port}/battlepass/admin/tasks.html?tab=trader");
            logger.Success("=========================================================");
        }
        catch (Exception ex)
        {
            logger.Warning($"[SPT-BattlePass] 无法解析访问地址: {ex.Message}");
        }
    }
}
