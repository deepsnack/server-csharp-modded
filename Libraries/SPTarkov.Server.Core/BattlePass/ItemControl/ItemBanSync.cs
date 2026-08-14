using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;

namespace SPTarkov.Server.Core.BattlePass.ItemControl;

/// <summary>
///     全局物品封禁（item-bans.json）。最小破坏原则——「屏蔽而非删除」：不改物品 DB、不动任何配置文件，
///     仅把封禁 tpl/分类注入各生成期共享黑名单（运行时增删即时生效、可还原）：
///     <list type="bullet">
///       <item><see cref="ItemConfig"/>.Blacklist —— bot 装备/改装件、PMC 战利品、空投（经 LootGenerator）、
///             fence、scavcase、邪教、跳蚤动态报价等凡走 <see cref="ItemFilterService.GetBlacklistedItems"/> 的生成途径；</item>
///       <item><see cref="ItemConfig"/>.LootableItemBlacklist —— 世界静态 + 散落战利品（LocationLootGenerator 生成期过滤）；</item>
///       <item><see cref="RagfairConfig"/>.Dynamic.Blacklist.Custom —— 跳蚤显式补刀。</item>
///     </list>
///     bot 携带战利品另由 <c>BotLootCacheService</c> 读同一 ItemConfig.Blacklist 过滤。
///     分类封禁展开为该类下全部 tpl 注入上述黑名单（不动 EnableCustomItemCategoryList 开关，避免与 FleaControlSync 抢占）。
///     仅托管本 mod 写入的条目；重放前先撤回上轮托管项，故对 vanilla 黑名单零侵入。
///     支持 mod 物品：按 tpl 即可，无需在原版基线内。
/// </summary>
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostDBModLoader + 90000)]
public class ItemBanSync(
    DatabaseService databaseService,
    ConfigServer configServer,
    ItemFilterService itemFilterService,
    BotLootCacheService botLootCacheService,
    ItemHelper itemHelper,
    ISptLogger<ItemBanSync> logger
) : IOnLoad
{
    private const string TraderPolicyKey = "battlepass-item-bans";
    private readonly HashSet<MongoId> _managedTpls = new();
    private HashSet<MongoId> _traderBlockedTpls = [];

    public Task OnLoad()
    {
        TraderAssortAccessPolicy.Register(TraderPolicyKey, FilterTraderAssort);
        Apply();
        return Task.CompletedTask;
    }

    /// <summary>应用全局封禁（幂等、可运行时重复调用）。先撤回上轮托管 tpl，再按当前配置重新注入。</summary>
    public void Apply()
    {
        try
        {
            var cfg = BattlePassStore.GetItemBans();
            var itemCfg = configServer.GetConfig<ItemConfig>();
            var fleaBlack = configServer.GetConfig<RagfairConfig>().Dynamic.Blacklist;

            // ---- 撤回上轮托管的 tpl（只删本 mod 加入的，绝不动 vanilla 原有项） ----
            foreach (var old in _managedTpls)
            {
                itemCfg.Blacklist.Remove(old);
                itemCfg.LootableItemBlacklist.Remove(old);
                fleaBlack.Custom.Remove(old);
            }

            _managedTpls.Clear();

            // ---- 直接按 tpl 封禁 ----
            foreach (var s in cfg.Tpls)
            {
                if (MongoIdEx.TryParse(s, out var id))
                {
                    BanTpl(id, itemCfg, fleaBlack);
                }
            }

            // ---- 按分类封禁：展开为该父类下全部 tpl ----
            var cats = new List<MongoId>();
            foreach (var s in cfg.Categories)
            {
                if (MongoIdEx.TryParse(s, out var c))
                {
                    cats.Add(c);
                }
            }

            if (cats.Count > 0)
            {
                foreach (var (tpl, _) in databaseService.GetItems())
                {
                    if (itemHelper.IsOfBaseclasses(tpl, cats))
                    {
                        BanTpl(tpl, itemCfg, fleaBlack);
                    }
                }
            }

            // ---- 让生成期缓存重建（散落/静态、主黑名单缓存）+ 清 bot 战利品池缓存，使增删即时生效 ----
            itemFilterService.RefreshBlacklistCaches();
            botLootCacheService.ClearCache();
            Volatile.Write(ref _traderBlockedTpls, _managedTpls.ToHashSet());

            logger.Success(
                $"[SPT-BattlePass] 全局物品封禁已应用（tpl {cfg.Tpls.Count}、分类 {cfg.Categories.Count} → 实际屏蔽 {_managedTpls.Count} 项）。"
            );
        }
        catch (Exception ex)
        {
            logger.Error($"[SPT-BattlePass] 全局物品封禁应用失败: {ex.Message}");
        }
    }

    private void BanTpl(MongoId id, ItemConfig itemCfg, RagfairBlacklist fleaBlack)
    {
        if (!_managedTpls.Add(id))
        {
            return; // 已处理
        }

        itemCfg.Blacklist.Add(id);
        itemCfg.LootableItemBlacklist.Add(id);
        fleaBlack.Custom.Add(id);
    }

    private TraderAssort FilterTraderAssort(MongoId sessionId, MongoId traderId, TraderAssort assort, bool isFlea)
    {
        var blockedTpls = Volatile.Read(ref _traderBlockedTpls);
        assort.RemoveItemsFromAssort(blockedTpls);
        return assort;
    }
}
