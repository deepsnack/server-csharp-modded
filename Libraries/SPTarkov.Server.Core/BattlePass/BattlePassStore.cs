using System.Collections.Concurrent;
using System.Text.Json;
using SPTarkov.Server.Core.BattlePass.ItemControl;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>
///     战斗通行证全部 JSON 持久化（SPT_Data/battlepass/ 下，user/mods 之外，不被 ModValidator/DatabaseImporter 扫描）。
///     单例服务，内部对每类文件加锁；进度按 profileId 分文件，缓存于内存并写穿。
/// </summary>
public static class BattlePassStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>tracks 字典中保留给「满级循环奖励」的等级键（正常等级从 1 起，0 专用于循环奖励双轨配置）。</summary>
    public const int CycleRewardLevelKey = 0;

    private static string BaseDir => Path.Combine(Directory.GetCurrentDirectory(), "SPT_Data", "battlepass");
    private static string SeasonPath => Path.Combine(BaseDir, "season.json");
    private static string TracksPath => Path.Combine(BaseDir, "tracks.json");
    private static string TasksPath => Path.Combine(BaseDir, "tasks.json");
    private static string GenTasksPath => Path.Combine(BaseDir, "tasks-gen.json");
    private static string CodesPath => Path.Combine(BaseDir, "codes.json");
    private static string OffersPath => Path.Combine(BaseDir, "trader.json");
    private static string ShopPath => Path.Combine(BaseDir, "shop.json");
    private static string ShopStatePath => Path.Combine(BaseDir, "shop-state.json");
    private static string GenSpecPath => Path.Combine(BaseDir, "task-gen.json");
    private static string TraderConfigPath => Path.Combine(BaseDir, "trader-meta.json");
    private static string ProgressDir => Path.Combine(BaseDir, "progress");
    private static string TitleCatalogPath => Path.Combine(BaseDir, "titles.json");

    /// <summary>玩家拥有/佩戴称号的目录（每 profile 一文件，跨赛季持久，独立于 progress/）。</summary>
    private static string TitlesDir => Path.Combine(BaseDir, "titles");

    /// <summary>称号图片目录（type=image 的称号，{id}.png）。</summary>
    public static string TitleImageDir => Path.Combine(BaseDir, "titles-img");

    /// <summary>某称号图片的绝对路径（约定 PNG）。</summary>
    public static string TitleImagePath(string titleId) => Path.Combine(TitleImageDir, titleId + ".png");

    private static string ItemOverridesPath => Path.Combine(BaseDir, "item-overrides.json");
    private static string FleaControlPath => Path.Combine(BaseDir, "flea-control.json");
    private static string ItemBansPath => Path.Combine(BaseDir, "item-bans.json");
    private static string CustomRecipesPath => Path.Combine(BaseDir, "custom-recipes.json");
    private static string QuestOverridesPath => Path.Combine(BaseDir, "quest-overrides.json");
    private static string CustomQuestsPath => Path.Combine(BaseDir, "custom-quests.json");
    private static string LotteryDir => Path.Combine(BaseDir, "lottery");
    private static string LotterySettingsPath => Path.Combine(LotteryDir, "settings.json");
    private static string LotteryPoolsPath => Path.Combine(LotteryDir, "pools.json");
    private static string LotteryShopPath => Path.Combine(LotteryDir, "shop.json");
    private static string LotteryDrawRecordsPath => Path.Combine(LotteryDir, "draw-records.json");
    private static string LotteryTransactionsPath => Path.Combine(LotteryDir, "transactions.json");
    private static string LotteryAuditLogsPath => Path.Combine(LotteryDir, "audit-logs.json");
    private static string LotteryWalletsDir => Path.Combine(LotteryDir, "wallets");
    private static string LotteryProgressDir => Path.Combine(LotteryDir, "progress");
    private static string LotteryShopPurchasesDir => Path.Combine(LotteryDir, "shop-purchases");

    /// <summary>商人头像文件的绝对路径（按配置的文件名，默认 trader-avatar.png）。</summary>
    public static string TraderAvatarPath(string? fileName = null)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? "trader-avatar.png" : Path.GetFileName(fileName);
        return Path.Combine(BaseDir, name);
    }

    private static readonly ConcurrentDictionary<string, BpProgress> ProgressCache = new();

    /// <summary>
    ///     每个 profile 一把互斥锁。<see cref="ProgressCache"/> 缓存的是<b>同一个</b> <c>BpProgress</c> 实例，
    ///     而浏览器打开通行证页会<b>并行</b>发起 state/tasks 等多个请求 → 多线程同时读写同一进度对象里的
    ///     <c>ActiveTasks</c>/<c>ClaimedFree</c> 等非并发集合，抛
    ///     "Operations that change non-concurrent collections must have exclusive access"。
    ///     用本锁把同一 profile 的进度访问串行化（不同 profile 仍并行）；配 <see cref="LockProfile"/> 使用。
    /// </summary>
    private static readonly ConcurrentDictionary<string, object> ProfileGates = new();

    /// <summary>
    ///     取得并进入指定 profile 的互斥区，返回的守卫在 <c>Dispose</c> 时退出。
    ///     用法：<c>using (BattlePassStore.LockProfile(profileId)) { GetProgress→Refresh→SaveProgress }</c>。
    /// </summary>
    public static IDisposable LockProfile(string profileId)
    {
        var gate = ProfileGates.GetOrAdd(profileId ?? string.Empty, _ => new object());
        return new ProfileLock(gate);
    }

    private sealed class ProfileLock : IDisposable
    {
        private readonly object _gate;
        private bool _released;

        public ProfileLock(object gate)
        {
            _gate = gate;
            Monitor.Enter(_gate);
        }

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            Monitor.Exit(_gate);
        }
    }

    private static readonly ConcurrentDictionary<string, BpPlayerTitles> PlayerTitlesCache = new();
    private static readonly ConcurrentDictionary<string, BpLotteryWallet> LotteryWalletCache = new();
    private static readonly ConcurrentDictionary<string, Dictionary<string, BpLotteryPoolProgress>> LotteryProgressCache = new();
    private static readonly ConcurrentDictionary<string, BpLotteryShopPurchaseProgress> LotteryShopPurchaseCache = new();

    /// <summary>启动时确保目录与默认配置存在（首次写入示例赛季/奖励轨/任务）。</summary>
    public static void EnsureSeeded()
    {
        Directory.CreateDirectory(BaseDir);
        Directory.CreateDirectory(ProgressDir);
        Directory.CreateDirectory(TitlesDir);
        Directory.CreateDirectory(TitleImageDir);
        Directory.CreateDirectory(LotteryDir);
        Directory.CreateDirectory(LotteryWalletsDir);
        Directory.CreateDirectory(LotteryProgressDir);
        Directory.CreateDirectory(LotteryShopPurchasesDir);

        if (!File.Exists(SeasonPath))
        {
            WriteJson(SeasonPath, BattlePassDefaults.Season());
        }

        if (!File.Exists(TracksPath))
        {
            WriteJson(TracksPath, BattlePassDefaults.Tracks());
        }

        if (!File.Exists(TasksPath))
        {
            WriteJson(TasksPath, BattlePassDefaults.Tasks());
        }

        if (!File.Exists(CodesPath))
        {
            WriteJson(CodesPath, new List<BpActivationCode>());
        }

        if (!File.Exists(TraderConfigPath))
        {
            WriteJson(TraderConfigPath, BattlePassDefaults.TraderConfig());
        }

        if (!File.Exists(OffersPath))
        {
            WriteJson(OffersPath, BattlePassDefaults.Offers());
        }

        if (!File.Exists(TitleCatalogPath))
        {
            WriteJson(TitleCatalogPath, BattlePassDefaults.TitleCatalog());
        }

        if (!File.Exists(ItemOverridesPath))
        {
            WriteJson(ItemOverridesPath, new List<BpItemOverride>());
        }

        if (!File.Exists(FleaControlPath))
        {
            WriteJson(FleaControlPath, new BpFleaControl());
        }

        if (!File.Exists(ItemBansPath))
        {
            WriteJson(ItemBansPath, new BpItemBans());
        }

        if (!File.Exists(QuestOverridesPath))
        {
            WriteJson(QuestOverridesPath, new List<BpQuestOverride>());
        }

        if (!File.Exists(CustomQuestsPath))
        {
            WriteJson(CustomQuestsPath, new List<BpCustomQuest>());
        }

        if (!File.Exists(LotterySettingsPath))
        {
            WriteJson(LotterySettingsPath, new BpLotterySettings());
        }

        if (!File.Exists(LotteryPoolsPath))
        {
            WriteJson(LotteryPoolsPath, new List<BpLotteryPool>());
        }

        if (!File.Exists(LotteryShopPath))
        {
            WriteJson(LotteryShopPath, new List<BpLotteryShopItem>());
        }

        if (!File.Exists(LotteryDrawRecordsPath))
        {
            WriteJson(LotteryDrawRecordsPath, new List<BpLotteryDrawRecord>());
        }

        if (!File.Exists(LotteryTransactionsPath))
        {
            WriteJson(LotteryTransactionsPath, new List<BpLotteryTransaction>());
        }

        if (!File.Exists(LotteryAuditLogsPath))
        {
            WriteJson(LotteryAuditLogsPath, new List<BpLotteryAuditLog>());
        }
    }

    // ---- 赛季 ----
    public static BpSeason GetSeason()
    {
        return ReadJson<BpSeason>(SeasonPath) ?? BattlePassDefaults.Season();
    }

    public static void SaveSeason(BpSeason season)
    {
        WriteJson(SeasonPath, season);
    }

    // ---- 奖励轨（level -> 双轨奖励） ----
    public static Dictionary<int, BpLevelRewards> GetTracks()
    {
        return ReadJson<Dictionary<int, BpLevelRewards>>(TracksPath) ?? BattlePassDefaults.Tracks();
    }

    public static void SaveTracks(Dictionary<int, BpLevelRewards> tracks)
    {
        WriteJson(TracksPath, tracks);
    }

    // ---- 任务模板 ----
    /// <summary>管理员手写的自定义任务池（不含程序生成的 gen_ 任务，后者独立存 <c>tasks-gen.json</c>）。</summary>
    public static List<BpTaskTemplate> GetTasks()
    {
        var tasks = ReadJson<List<BpTaskTemplate>>(TasksPath) ?? BattlePassDefaults.Tasks();
        // 历史兼容：旧版把 gen_ 任务混存于此污染后台任务池——自定义池一律剔除生成任务。
        tasks.RemoveAll(t => t.Id is not null && t.Id.StartsWith(TaskGeneratorService.GenPrefix, StringComparison.OrdinalIgnoreCase));
        return tasks;
    }

    public static void SaveTasks(List<BpTaskTemplate> tasks)
    {
        WriteJson(TasksPath, tasks);
    }

    /// <summary>程序生成的任务池（gen_ 前缀，与自定义池物理隔离，多次刷新只整体替换、不堆积于后台管理）。</summary>
    public static List<BpTaskTemplate> GetGenTasks()
    {
        var existing = ReadJson<List<BpTaskTemplate>>(GenTasksPath);
        if (existing is not null)
        {
            return existing;
        }

        // 首次访问：从旧版混存的 tasks.json 迁移已生成的 gen_ 任务，避免升级后已生成任务突然消失。
        var migrated = (ReadJson<List<BpTaskTemplate>>(TasksPath) ?? new List<BpTaskTemplate>())
            .Where(t => t.Id is not null && t.Id.StartsWith(TaskGeneratorService.GenPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        WriteJson(GenTasksPath, migrated);
        return migrated;
    }

    public static void SaveGenTasks(List<BpTaskTemplate> tasks)
    {
        WriteJson(GenTasksPath, tasks);
    }

    /// <summary>自定义池 ∪ 生成池：玩家抽取活跃任务与模板查询使用全量。</summary>
    public static List<BpTaskTemplate> GetAllTasks()
    {
        var all = GetTasks();
        all.AddRange(GetGenTasks());
        return all;
    }

    // ---- 激活码 ----
    public static List<BpActivationCode> GetCodes()
    {
        return ReadJson<List<BpActivationCode>>(CodesPath) ?? new List<BpActivationCode>();
    }

    public static void SaveCodes(List<BpActivationCode> codes)
    {
        WriteJson(CodesPath, codes);
    }

    // ---- 自定义藏身处配方 ----
    public static List<BpCustomRecipe> GetCustomRecipes()
    {
        return ReadJson<List<BpCustomRecipe>>(CustomRecipesPath) ?? new List<BpCustomRecipe>();
    }

    public static void SaveCustomRecipes(List<BpCustomRecipe> recipes)
    {
        WriteJson(CustomRecipesPath, recipes);
    }

    // ---- 商人货架 offers ----
    public static List<BpTraderOffer> GetOffers()
    {
        return ReadJson<List<BpTraderOffer>>(OffersPath) ?? new List<BpTraderOffer>();
    }

    public static void SaveOffers(List<BpTraderOffer> offers)
    {
        WriteJson(OffersPath, offers);
    }

    // ---- 网页商店自定义货架（独立于游戏内商人 offers；管理员可单独维护）----
    public static List<BpTraderOffer> GetShopOffers()
    {
        return ReadJson<List<BpTraderOffer>>(ShopPath) ?? new List<BpTraderOffer>();
    }

    public static void SaveShopOffers(List<BpTraderOffer> offers)
    {
        WriteJson(ShopPath, offers);
    }

    public static BpShopState GetShopState()
    {
        var state = ReadJson<BpShopState>(ShopStatePath) ?? new BpShopState();
        state.Sales ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        state.OfferPeriods ??= new Dictionary<string, BpShopOfferPeriod>(StringComparer.OrdinalIgnoreCase);
        return state;
    }

    public static void SaveShopState(BpShopState state)
    {
        WriteJson(ShopStatePath, state);
    }

    // ---- 任务生成规格 ----
    public static BpGenSpec GetGenSpec()
    {
        var spec = ReadJson<BpGenSpec>(GenSpecPath) ?? new BpGenSpec();
        spec.Daily ??= new BpGenScopeSpec();
        spec.Weekly ??= new BpGenScopeSpec();
        spec.Season ??= new BpGenScopeSpec();
        return spec;
    }

    public static void SaveGenSpec(BpGenSpec spec)
    {
        WriteJson(GenSpecPath, spec);
    }

    // ---- 商人元信息 ----
    public static BpTraderConfig GetTraderConfig()
    {
        return ReadJson<BpTraderConfig>(TraderConfigPath) ?? BattlePassDefaults.TraderConfig();
    }

    public static void SaveTraderConfig(BpTraderConfig config)
    {
        WriteJson(TraderConfigPath, config);
    }

    // ---- 玩家进度 ----
    public static BpProgress GetProgress(string profileId)
    {
        if (ProgressCache.TryGetValue(profileId, out var cached))
        {
            return cached;
        }

        var path = Path.Combine(ProgressDir, profileId + ".json");
        var prog = ReadJson<BpProgress>(path) ?? new BpProgress { SeasonId = GetSeason().SeasonId };
        ProgressCache[profileId] = prog;
        return prog;
    }

    public static void SaveProgress(string profileId, BpProgress progress)
    {
        ProgressCache[profileId] = progress;
        var path = Path.Combine(ProgressDir, profileId + ".json");
        WriteJson(path, progress);
    }

    /// <summary>重置指定玩家当前赛季通行证进度；可选择保留付费轨解锁状态。</summary>
    public static BpProgress ResetProgress(string profileId, BpSeason season, bool preservePremium)
    {
        var old = GetProgress(profileId);
        var progress = new BpProgress
        {
            SeasonId = season.SeasonId,
            PremiumUnlocked = preservePremium && old.PremiumUnlocked,
            RewardLedgerInitialized = true,
        };

        SaveProgress(profileId, progress);
        return progress;
    }

    /// <summary>列出所有有进度的 profileId（管理员总览用）。</summary>
    public static List<string> ListProgressProfileIds()
    {
        return ListJsonProfileIds(ProgressDir);
    }

    // ---- 称号目录（全局，管理员定义） ----
    public static List<BpTitle> GetTitleCatalog()
    {
        return ReadJson<List<BpTitle>>(TitleCatalogPath) ?? BattlePassDefaults.TitleCatalog();
    }

    public static void SaveTitleCatalog(List<BpTitle> titles)
    {
        WriteJson(TitleCatalogPath, titles);
    }

    // ---- 玩家拥有/佩戴的称号（每 profile 一文件，跨赛季持久） ----
    public static BpPlayerTitles GetPlayerTitles(string profileId)
    {
        if (PlayerTitlesCache.TryGetValue(profileId, out var cached))
        {
            return cached;
        }

        var path = Path.Combine(TitlesDir, profileId + ".json");
        var t = ReadJson<BpPlayerTitles>(path) ?? new BpPlayerTitles();
        PlayerTitlesCache[profileId] = t;
        return t;
    }

    public static void SavePlayerTitles(string profileId, BpPlayerTitles titles)
    {
        PlayerTitlesCache[profileId] = titles;
        var path = Path.Combine(TitlesDir, profileId + ".json");
        WriteJson(path, titles);
    }

    /// <summary>给玩家授予一个称号 id。已拥有返回 false。</summary>
    public static bool GrantTitle(string profileId, string titleId)
    {
        if (string.IsNullOrWhiteSpace(titleId))
        {
            return false;
        }

        var t = GetPlayerTitles(profileId);
        if (!t.Owned.Add(titleId))
        {
            return false;
        }

        SavePlayerTitles(profileId, t);
        return true;
    }

    /// <summary>撤销玩家的某称号；若正佩戴则一并卸下。未拥有返回 false。</summary>
    public static bool RevokeTitle(string profileId, string titleId)
    {
        var t = GetPlayerTitles(profileId);
        if (!t.Owned.Remove(titleId))
        {
            return false;
        }

        if (t.Equipped == titleId)
        {
            t.Equipped = null;
        }

        SavePlayerTitles(profileId, t);
        return true;
    }

    /// <summary>佩戴称号（须已拥有；传 null/空 = 卸下）。返回是否成功。</summary>
    public static bool EquipTitle(string profileId, string? titleId)
    {
        var t = GetPlayerTitles(profileId);
        if (string.IsNullOrWhiteSpace(titleId))
        {
            t.Equipped = null;
            SavePlayerTitles(profileId, t);
            return true;
        }

        if (!t.Owned.Contains(titleId))
        {
            return false;
        }

        t.Equipped = titleId;
        SavePlayerTitles(profileId, t);
        return true;
    }

    /// <summary>列出所有持有称号记录的 profileId（管理员总览用）。</summary>
    public static List<string> ListPlayerTitleProfileIds()
    {
        return ListJsonProfileIds(TitlesDir);
    }

    // ---- 物品获取途径 override ----
    public static List<BpItemOverride> GetItemOverrides()
    {
        return ReadJson<List<BpItemOverride>>(ItemOverridesPath) ?? new List<BpItemOverride>();
    }

    public static void SaveItemOverrides(List<BpItemOverride> overrides)
    {
        WriteJson(ItemOverridesPath, overrides);
    }

    // ---- 商人任务：原版覆盖 ----
    public static List<BpQuestOverride> GetQuestOverrides()
    {
        return ReadJson<List<BpQuestOverride>>(QuestOverridesPath) ?? new List<BpQuestOverride>();
    }

    public static void SaveQuestOverrides(List<BpQuestOverride> overrides)
    {
        WriteJson(QuestOverridesPath, overrides);
    }

    // ---- 商人任务：自定义任务 ----
    public static List<BpCustomQuest> GetCustomQuests()
    {
        return ReadJson<List<BpCustomQuest>>(CustomQuestsPath) ?? new List<BpCustomQuest>();
    }

    public static void SaveCustomQuests(List<BpCustomQuest> quests)
    {
        WriteJson(CustomQuestsPath, quests);
    }

    // ---- 跳蚤黑名单接管配置 ----
    public static BpFleaControl GetFleaControl()
    {
        return ReadJson<BpFleaControl>(FleaControlPath) ?? new BpFleaControl();
    }

    public static void SaveFleaControl(BpFleaControl config)
    {
        WriteJson(FleaControlPath, config);
    }

    // ---- 全局物品封禁配置 ----
    public static BpItemBans GetItemBans()
    {
        return ReadJson<BpItemBans>(ItemBansPath) ?? new BpItemBans();
    }

    public static void SaveItemBans(BpItemBans config)
    {
        WriteJson(ItemBansPath, config);
    }

    // ---- 抽奖模块：配置 ----
    public static BpLotterySettings GetLotterySettings()
    {
        return ReadJson<BpLotterySettings>(LotterySettingsPath) ?? new BpLotterySettings();
    }

    public static void SaveLotterySettings(BpLotterySettings settings)
    {
        WriteJson(LotterySettingsPath, settings);
    }

    public static List<BpLotteryPool> GetLotteryPools()
    {
        return ReadJson<List<BpLotteryPool>>(LotteryPoolsPath) ?? new List<BpLotteryPool>();
    }

    public static void SaveLotteryPools(List<BpLotteryPool> pools)
    {
        WriteJson(LotteryPoolsPath, pools);
    }

    public static List<BpLotteryShopItem> GetLotteryShopItems()
    {
        return ReadJson<List<BpLotteryShopItem>>(LotteryShopPath) ?? new List<BpLotteryShopItem>();
    }

    public static void SaveLotteryShopItems(List<BpLotteryShopItem> items)
    {
        WriteJson(LotteryShopPath, items);
    }

    // ---- 抽奖模块：玩家钱包 ----
    public static BpLotteryWallet GetLotteryWallet(string profileId)
    {
        if (LotteryWalletCache.TryGetValue(profileId, out var cached))
        {
            return cached;
        }

        var wallet = ReadJson<BpLotteryWallet>(LotteryWalletPath(profileId)) ?? new BpLotteryWallet { ProfileId = profileId };
        wallet.ProfileId = profileId;
        wallet.PoolTickets ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        LotteryWalletCache[profileId] = wallet;
        return wallet;
    }

    public static void SaveLotteryWallet(string profileId, BpLotteryWallet wallet)
    {
        wallet.ProfileId = profileId;
        wallet.PoolTickets ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        wallet.UpdatedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        LotteryWalletCache[profileId] = wallet;
        WriteJson(LotteryWalletPath(profileId), wallet);
    }

    public static List<string> ListLotteryWalletProfileIds()
    {
        return ListJsonProfileIds(LotteryWalletsDir);
    }

    // ---- 抽奖模块：奖池进度 ----
    public static BpLotteryPoolProgress GetLotteryPoolProgress(string profileId, string poolId)
    {
        var map = GetLotteryProgressMap(profileId);
        if (map.TryGetValue(poolId, out var progress))
        {
            return progress;
        }

        progress = new BpLotteryPoolProgress { ProfileId = profileId, PoolId = poolId };
        map[poolId] = progress;
        return progress;
    }

    public static void SaveLotteryPoolProgress(string profileId, BpLotteryPoolProgress progress)
    {
        var map = GetLotteryProgressMap(profileId);
        progress.ProfileId = profileId;
        progress.UpdatedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        map[progress.PoolId] = progress;
        LotteryProgressCache[profileId] = map;
        WriteJson(LotteryProgressPath(profileId), map.Values.ToList());
    }

    public static bool ResetLotteryPoolProgress(string profileId, string poolId)
    {
        var map = GetLotteryProgressMap(profileId);
        var removed = map.Remove(poolId);
        if (removed)
        {
            LotteryProgressCache[profileId] = map;
            WriteJson(LotteryProgressPath(profileId), map.Values.ToList());
        }

        return removed;
    }

    public static int ResetLotteryPoolProgressForAll(string poolId)
    {
        var count = 0;
        foreach (var profileId in ListLotteryProgressProfileIds())
        {
            if (ResetLotteryPoolProgress(profileId, poolId))
            {
                count++;
            }
        }

        return count;
    }

    public static List<string> ListLotteryProgressProfileIds()
    {
        return ListJsonProfileIds(LotteryProgressDir);
    }

    private static Dictionary<string, BpLotteryPoolProgress> GetLotteryProgressMap(string profileId)
    {
        if (LotteryProgressCache.TryGetValue(profileId, out var cached))
        {
            return cached;
        }

        var list = ReadJson<List<BpLotteryPoolProgress>>(LotteryProgressPath(profileId)) ?? new List<BpLotteryPoolProgress>();
        var map = list
            .Where(p => !string.IsNullOrWhiteSpace(p.PoolId))
            .GroupBy(p => p.PoolId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g =>
            {
                var p = g.Last();
                p.ProfileId = profileId;
                p.DrawnPrizeIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                return p;
            }, StringComparer.OrdinalIgnoreCase);

        LotteryProgressCache[profileId] = map;
        return map;
    }

    // ---- 抽奖模块：记录与事务 ----
    public static List<BpLotteryDrawRecord> GetLotteryDrawRecords()
    {
        return ReadJson<List<BpLotteryDrawRecord>>(LotteryDrawRecordsPath) ?? new List<BpLotteryDrawRecord>();
    }

    public static void SaveLotteryDrawRecords(List<BpLotteryDrawRecord> records)
    {
        WriteJson(LotteryDrawRecordsPath, records);
    }

    public static void AppendLotteryDrawRecords(IEnumerable<BpLotteryDrawRecord> newRecords)
    {
        lock (Gate)
        {
            var records = ReadJson<List<BpLotteryDrawRecord>>(LotteryDrawRecordsPath) ?? new List<BpLotteryDrawRecord>();
            records.AddRange(newRecords);
            WriteJson(LotteryDrawRecordsPath, records);
        }
    }

    public static List<BpLotteryTransaction> GetLotteryTransactions()
    {
        return ReadJson<List<BpLotteryTransaction>>(LotteryTransactionsPath) ?? new List<BpLotteryTransaction>();
    }

    public static void SaveLotteryTransactions(List<BpLotteryTransaction> transactions)
    {
        WriteJson(LotteryTransactionsPath, transactions);
    }

    public static void UpsertLotteryTransaction(BpLotteryTransaction transaction)
    {
        lock (Gate)
        {
            var transactions = ReadJson<List<BpLotteryTransaction>>(LotteryTransactionsPath) ?? new List<BpLotteryTransaction>();
            var index = transactions.FindIndex(t =>
                (!string.IsNullOrWhiteSpace(transaction.Id) && string.Equals(t.Id, transaction.Id, StringComparison.OrdinalIgnoreCase))
                || (string.Equals(t.ProfileId, transaction.ProfileId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(t.RequestId, transaction.RequestId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(t.Action, transaction.Action, StringComparison.OrdinalIgnoreCase)));

            if (index >= 0)
            {
                transactions[index] = transaction;
            }
            else
            {
                transactions.Add(transaction);
            }

            WriteJson(LotteryTransactionsPath, transactions);
        }
    }

    public static BpLotteryTransaction? FindLotteryTransaction(string profileId, string requestId, string action)
    {
        return GetLotteryTransactions().FirstOrDefault(t =>
            string.Equals(t.ProfileId, profileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(t.RequestId, requestId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(t.Action, action, StringComparison.OrdinalIgnoreCase));
    }

    // ---- 抽奖模块：兑换商店购买计数 ----
    public static BpLotteryShopPurchaseProgress GetLotteryShopPurchases(string profileId)
    {
        if (LotteryShopPurchaseCache.TryGetValue(profileId, out var cached))
        {
            return cached;
        }

        var progress = ReadJson<BpLotteryShopPurchaseProgress>(LotteryShopPurchasesPath(profileId))
            ?? new BpLotteryShopPurchaseProgress { ProfileId = profileId };
        progress.ProfileId = profileId;
        progress.Items ??= new Dictionary<string, BpLotteryShopPurchaseCounter>(StringComparer.OrdinalIgnoreCase);
        LotteryShopPurchaseCache[profileId] = progress;
        return progress;
    }

    public static void SaveLotteryShopPurchases(string profileId, BpLotteryShopPurchaseProgress progress)
    {
        progress.ProfileId = profileId;
        progress.Items ??= new Dictionary<string, BpLotteryShopPurchaseCounter>(StringComparer.OrdinalIgnoreCase);
        LotteryShopPurchaseCache[profileId] = progress;
        WriteJson(LotteryShopPurchasesPath(profileId), progress);
    }

    // ---- 抽奖模块：审计日志 ----
    public static List<BpLotteryAuditLog> GetLotteryAuditLogs()
    {
        return ReadJson<List<BpLotteryAuditLog>>(LotteryAuditLogsPath) ?? new List<BpLotteryAuditLog>();
    }

    public static void AppendLotteryAuditLog(BpLotteryAuditLog log)
    {
        lock (Gate)
        {
            var logs = ReadJson<List<BpLotteryAuditLog>>(LotteryAuditLogsPath) ?? new List<BpLotteryAuditLog>();
            logs.Add(log);
            WriteJson(LotteryAuditLogsPath, logs);
        }
    }

    private static string LotteryWalletPath(string profileId) => Path.Combine(LotteryWalletsDir, profileId + ".json");

    private static string LotteryProgressPath(string profileId) => Path.Combine(LotteryProgressDir, profileId + ".json");

    private static string LotteryShopPurchasesPath(string profileId) => Path.Combine(LotteryShopPurchasesDir, profileId + ".json");

    private static List<string> ListJsonProfileIds(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                return new List<string>();
            }

            return Directory
                .EnumerateFiles(dir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(x => !string.IsNullOrEmpty(x))
                .Select(x => x!)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    // ---- 底层读写 ----
    private static T? ReadJson<T>(string path)
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return default;
                }

                return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
            }
            catch
            {
                return default;
            }
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOpts));
            }
            catch
            {
                // 写盘失败不应中断请求
            }
        }
    }
}
