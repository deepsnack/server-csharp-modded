using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;

namespace SPTarkov.Server.Core.BattlePass.ItemControl;

/// <summary>
///     跳蚤黑名单接管：把 <c>flea-control.json</c> 应用到共享单例 <see cref="RagfairConfig"/>.Dynamic.Blacklist
///     （实时读，OnLoad 期变更即生效）。只增删本 mod 托管的条目，不动 vanilla ragfair.json 原有项；
///     白名单/原生开关在首次接管时快照原值，撤销时还原。可运行时重复 <see cref="Apply"/>（后台保存触发）。
/// </summary>
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostDBModLoader + 90000)]
public class FleaControlSync(
    DatabaseService databaseService,
    ConfigServer configServer,
    ISptLogger<FleaControlSync> logger
) : IOnLoad
{
    private readonly HashSet<MongoId> _managedBlacklist = new();
    private readonly HashSet<MongoId> _managedCategories = new();
    private readonly Dictionary<MongoId, bool?> _originalCanSell = new(); // whitelist 还原用
    private bool _togglesSnapshotted;
    private bool _origBsg, _origQuest, _origTrader, _origDamaged, _origCategory;

    public Task OnLoad()
    {
        Apply();
        return Task.CompletedTask;
    }

    public void Apply()
    {
        try
        {
            var cfg = BattlePassStore.GetFleaControl();
            var blacklist = configServer.GetConfig<RagfairConfig>().Dynamic.Blacklist;
            var items = databaseService.GetItems();

            if (!_togglesSnapshotted)
            {
                _origBsg = blacklist.EnableBsgList;
                _origQuest = blacklist.EnableQuestList;
                _origTrader = blacklist.TraderItems;
                _origDamaged = blacklist.DamagedAmmoPacks;
                _origCategory = blacklist.EnableCustomItemCategoryList;
                _togglesSnapshotted = true;
            }

            // ---- 撤回上轮托管的 tpl / 分类，再按当前配置重新加入 ----
            foreach (var old in _managedBlacklist)
            {
                blacklist.Custom.Remove(old);
            }

            foreach (var old in _managedCategories)
            {
                blacklist.CustomItemCategoryList.Remove(old);
            }

            _managedBlacklist.Clear();
            _managedCategories.Clear();

            foreach (var s in cfg.BlacklistTpls)
            {
                if (MongoIdEx.TryParse(s, out var id))
                {
                    blacklist.Custom.Add(id);
                    _managedBlacklist.Add(id);
                }
            }

            foreach (var s in cfg.BlacklistCategories)
            {
                if (MongoIdEx.TryParse(s, out var id))
                {
                    blacklist.CustomItemCategoryList.Add(id);
                    _managedCategories.Add(id);
                }
            }

            // ---- 白名单：放开 CanSellOnRagfair（快照原值，撤销时还原） ----
            var wantWhitelist = new HashSet<MongoId>();
            foreach (var s in cfg.WhitelistTpls)
            {
                if (!MongoIdEx.TryParse(s, out var id) || !items.TryGetValue(id, out var item) || item.Properties is null)
                {
                    continue;
                }

                wantWhitelist.Add(id);
                if (!_originalCanSell.ContainsKey(id))
                {
                    _originalCanSell[id] = item.Properties.CanSellOnRagfair;
                }

                item.Properties.CanSellOnRagfair = true;
                blacklist.Custom.Remove(id); // 白名单优先于自定义黑名单
            }

            // 已不在白名单的、之前被我们放开的 → 还原原值
            foreach (var prev in _originalCanSell.Keys.ToList())
            {
                if (!wantWhitelist.Contains(prev) && items.TryGetValue(prev, out var item) && item.Properties is not null)
                {
                    item.Properties.CanSellOnRagfair = _originalCanSell[prev];
                    _originalCanSell.Remove(prev);
                }
            }

            // ---- 原生开关（null = 不接管，还原为快照原值） ----
            blacklist.EnableBsgList = cfg.EnableBsgList ?? _origBsg;
            blacklist.EnableQuestList = cfg.EnableQuestList ?? _origQuest;
            blacklist.TraderItems = cfg.TraderItems ?? _origTrader;
            blacklist.DamagedAmmoPacks = cfg.DamagedAmmoPacks ?? _origDamaged;
            blacklist.EnableCustomItemCategoryList = cfg.EnableCustomItemCategoryList ?? _origCategory;

            logger.Success(
                $"[SPT-BattlePass] 跳蚤黑名单接管已应用（黑名单 {_managedBlacklist.Count}、分类 {_managedCategories.Count}、白名单 {wantWhitelist.Count}）。"
            );
        }
        catch (Exception ex)
        {
            logger.Error($"[SPT-BattlePass] 跳蚤黑名单接管失败: {ex.Message}");
        }
    }
}
