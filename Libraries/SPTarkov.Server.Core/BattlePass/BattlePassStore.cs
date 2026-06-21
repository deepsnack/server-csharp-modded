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
    private static string CodesPath => Path.Combine(BaseDir, "codes.json");
    private static string OffersPath => Path.Combine(BaseDir, "trader.json");
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

    /// <summary>商人头像文件的绝对路径（按配置的文件名，默认 trader-avatar.png）。</summary>
    public static string TraderAvatarPath(string? fileName = null)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? "trader-avatar.png" : Path.GetFileName(fileName);
        return Path.Combine(BaseDir, name);
    }

    private static readonly ConcurrentDictionary<string, BpProgress> ProgressCache = new();
    private static readonly ConcurrentDictionary<string, BpPlayerTitles> PlayerTitlesCache = new();

    /// <summary>启动时确保目录与默认配置存在（首次写入示例赛季/奖励轨/任务）。</summary>
    public static void EnsureSeeded()
    {
        Directory.CreateDirectory(BaseDir);
        Directory.CreateDirectory(ProgressDir);
        Directory.CreateDirectory(TitlesDir);
        Directory.CreateDirectory(TitleImageDir);

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
    public static List<BpTaskTemplate> GetTasks()
    {
        var tasks = ReadJson<List<BpTaskTemplate>>(TasksPath);
        return tasks ?? BattlePassDefaults.Tasks();
    }

    public static void SaveTasks(List<BpTaskTemplate> tasks)
    {
        WriteJson(TasksPath, tasks);
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
        try
        {
            if (!Directory.Exists(ProgressDir))
            {
                return new List<string>();
            }

            return Directory
                .EnumerateFiles(ProgressDir, "*.json")
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
        try
        {
            if (!Directory.Exists(TitlesDir))
            {
                return new List<string>();
            }

            return Directory
                .EnumerateFiles(TitlesDir, "*.json")
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

    // ---- 物品获取途径 override ----
    public static List<BpItemOverride> GetItemOverrides()
    {
        return ReadJson<List<BpItemOverride>>(ItemOverridesPath) ?? new List<BpItemOverride>();
    }

    public static void SaveItemOverrides(List<BpItemOverride> overrides)
    {
        WriteJson(ItemOverridesPath, overrides);
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
