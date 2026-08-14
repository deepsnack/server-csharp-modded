using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Game;

namespace SPTarkov.Server.Core.Models.Spt.Config;

public record CoreConfig : BaseConfig
{
    [JsonPropertyName("kind")]
    public override string Kind { get; set; } = "spt-core";

    [JsonPropertyName("projectName")]
    public required string ProjectName { get; set; }

    /// <summary>
    ///     版本号短横线后缀（展示层）。非空时版本展示面变为 "{主版本}-{此后缀}"，
    ///     如 "4.0.13-测试服"。留空 = 维持原编译期行为（debug 显示 bleeding edge 文案、游戏内显示 commit）。
    ///     中文支持范围：launcher / 控制台水印 / 网页 / 存档元数据均可中文（JSON UTF-8）；
    ///     仅"游戏内左下角版本标签"一面受 HTTP 头 ASCII 协议限制（客户端会把该 label 经
    ///     Taxonomy.Major 拼进 App-Version 请求头，非 ASCII 会让客户端报
    ///     "Header value contains invalid characters" 拒绝启动）——该面遇非 ASCII 后缀自动回退原版格式。
    ///     只影响展示：升级/兼容性校验仍走编译期 SemVer（ProgramStatics.SPT_VERSION）。
    /// </summary>
    [JsonPropertyName("versionSuffix")]
    public string? VersionSuffix { get; set; }

    [JsonPropertyName("compatibleTarkovVersion")]
    public required string CompatibleTarkovVersion { get; set; }

    [JsonPropertyName("serverName")]
    public required string ServerName { get; set; }

    [JsonPropertyName("profileSaveIntervalSeconds")]
    public required int ProfileSaveIntervalInSeconds { get; set; }

    /// <summary>
    ///     Every N periodic save cycles, SaveServer saves ALL in-memory profiles regardless of dirty
    ///     state. Backstop for write paths that mutate profiles in place without marking them dirty.
    ///     Default 5 (about 5 minutes at the default 60s interval). Set to 1 to restore old behavior.
    /// </summary>
    [JsonPropertyName("profileFullSaveEveryNCycles")]
    public int ProfileFullSaveEveryNCycles { get; set; } = 5;

    [JsonPropertyName("sptFriendNickname")]
    public required string SptFriendNickname { get; set; }

    [JsonPropertyName("allowProfileWipe")]
    public required bool AllowProfileWipe { get; set; }

    [JsonPropertyName("bsgLogging")]
    public required BsgLogging BsgLogging { get; set; }

    [JsonPropertyName("release")]
    public required Release Release { get; set; }

    [JsonPropertyName("fixes")]
    public required GameFixes Fixes { get; set; }

    [JsonPropertyName("survey")]
    public required SurveyResponseData Survey { get; set; }

    [JsonPropertyName("features")]
    public required ServerFeatures Features { get; set; }

    [JsonPropertyName("enableNoGCRegions")]
    // ReSharper disable once InconsistentNaming
    public required bool EnableNoGCRegions { get; set; }

    // ReSharper disable once InconsistentNaming
    private int _noGCRegionMaxMemoryGB = 4;

    [JsonPropertyName("noGCRegionMaxMemoryGB")]
    // ReSharper disable once InconsistentNaming
    public required int NoGCRegionMaxMemoryGB
    {
        get
        {
            return _noGCRegionMaxMemoryGB;
        }

        set
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    $"Invalid value: {nameof(NoGCRegionMaxMemoryGB)}: {value}. Must be greater than zero."
                );
            }
            _noGCRegionMaxMemoryGB = value;
        }
    }

    // ReSharper disable once InconsistentNaming
    private int _noGCRegionMaxLOHMemoryGB = 3;

    [JsonPropertyName("noGCRegionMaxLOHMemoryGB")]
    // ReSharper disable once InconsistentNaming
    public required int NoGCRegionMaxLOHMemoryGB
    {
        get
        {
            return _noGCRegionMaxLOHMemoryGB;
        }

        set
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    $"Invalid value {nameof(NoGCRegionMaxLOHMemoryGB)}: {value}. Must be greater than zero."
                );
            }
            _noGCRegionMaxLOHMemoryGB = value;
        }
    }

    /// <summary>
    ///     Commit hash build server was created from
    /// </summary>
    [JsonPropertyName("commit")]
    public string? Commit { get; set; }

    /// <summary>
    ///     Timestamp of server build
    /// </summary>
    [JsonPropertyName("buildTime")]
    public string? BuildTime { get; set; }

    /// <summary>
    ///     Timestamp of server start up
    /// </summary>
    [JsonPropertyName("serverStartTime")]
    public long? ServerStartTime { get; set; }

    /// <summary>
    ///     Server locale keys that will be added to the bottom of the startup watermark
    /// </summary>
    [JsonPropertyName("customWatermarkLocaleKeys")]
    public List<string>? CustomWatermarkLocaleKeys { get; set; }

    /// <summary>
    ///     SMTP 邮件配置
    /// </summary>
    [JsonPropertyName("smtpConfig")]
    public SmtpConfig? SmtpConfig { get; set; }
}

public record BsgLogging
{
    /// <summary>
    ///     verbosity of what to log, yes I know this is backwards, but its how nlog deals with ordinals. <br />
    ///     complain to them about it! In all cases, better exceptions will be logged.<br />
    ///     WARNING: trace-info logging will quickly create log files in the megabytes.<br />
    ///     0 - trace<br />
    ///     1 - debug<br />
    ///     2 - info<br />
    ///     3 - warn<br />
    ///     4 - error<br />
    ///     5 - fatal<br />
    ///     6 - off
    /// </summary>
    [JsonPropertyName("verbosity")]
    public int Verbosity { get; set; }

    /// <summary>
    ///     Should we send the logging to the server
    /// </summary>
    [JsonPropertyName("sendToServer")]
    public bool SendToServer { get; set; }
}

public record Release
{
    /// <summary>
    ///     Disclaimer outlining the intended usage of bleeding edge
    /// </summary>
    [JsonPropertyName("betaDisclaimerText")]
    public string? BetaDisclaimerText { get; set; }

    /// <summary>
    ///     Text logged when users agreed to terms
    /// </summary>
    [JsonPropertyName("betaDisclaimerAcceptText")]
    public string? BetaDisclaimerAcceptText { get; set; }

    /// <summary>
    ///     Server mods loaded message
    /// </summary>
    [JsonPropertyName("serverModsLoadedText")]
    public string? ServerModsLoadedText { get; set; }

    /// <summary>
    ///     Server mods loaded debug message text
    /// </summary>
    [JsonPropertyName("serverModsLoadedDebugText")]
    public string? ServerModsLoadedDebugText { get; set; }

    /// <summary>
    ///     Client mods loaded message
    /// </summary>
    [JsonPropertyName("clientModsLoadedText")]
    public string? ClientModsLoadedText { get; set; }

    /// <summary>
    ///     Client mods loaded debug message text
    /// </summary>
    [JsonPropertyName("clientModsLoadedDebugText")]
    public string? ClientModsLoadedDebugText { get; set; }

    /// <summary>
    ///     Illegal plugins log message
    /// </summary>
    [JsonPropertyName("illegalPluginsLoadedText")]
    public string? IllegalPluginsLoadedText { get; set; }

    /// <summary>
    ///     Illegal plugins exception
    /// </summary>
    [JsonPropertyName("illegalPluginsExceptionText")]
    public string? IllegalPluginsExceptionText { get; set; }

    /// <summary>
    ///     Summary of release changes
    /// </summary>
    [JsonPropertyName("releaseSummaryText")]
    public string? ReleaseSummaryText { get; set; }

    /// <summary>
    ///     Enables the cool watermark in-game
    /// </summary>
    [JsonPropertyName("isBeta")]
    public bool? IsBeta { get; set; }

    /// <summary>
    ///     Whether mods are enabled
    /// </summary>
    [JsonPropertyName("isModdable")]
    public bool? IsModdable { get; set; }

    /// <summary>
    ///     Are mods loaded on the server?
    /// </summary>
    [JsonPropertyName("isModded")]
    public bool IsModded { get; set; }

    /// <summary>
    ///     How long before the messagebox times out and closes the game
    /// </summary>
    [JsonPropertyName("betaDisclaimerTimeoutDelay")]
    public int BetaDisclaimerTimeoutDelay { get; set; }
}

public record GameFixes
{
    /// <summary>
    ///     Shotguns use a different value than normal guns causing huge pellet dispersion
    /// </summary>
    [JsonPropertyName("fixShotgunDispersion")]
    public bool FixShotgunDispersion { get; set; }

    [JsonPropertyName("shotgunIdsToFix")]
    public IEnumerable<MongoId> ShotgunIdsToFix { get; set; }

    /// <summary>
    ///     Remove items added by mods when the mod no longer exists - can fix dead profiles stuck at game load
    /// </summary>
    [JsonPropertyName("removeModItemsFromProfile")]
    public bool RemoveModItemsFromProfile { get; set; }

    /// <summary>
    ///     Remove invalid traders from profile - trader data can be leftover when player removes trader mod
    /// </summary>
    [JsonPropertyName("removeInvalidTradersFromProfile")]
    public bool RemoveInvalidTradersFromProfile { get; set; }

    /// <summary>
    ///     Fix issues that cause the game to not start due to inventory item issues
    /// </summary>
    [JsonPropertyName("fixProfileBreakingInventoryItemIssues")]
    public bool FixProfileBreakingInventoryItemIssues { get; set; }

    /// <summary>
    /// Should pre-raid english locales be renamed during raid start
    /// </summary>
    [JsonPropertyName("renamePreRaidLocales")]
    public bool RenamePreRaidLocales { get; set; }
}

public record ServerFeatures
{
    [JsonPropertyName("compressProfile")]
    public bool CompressProfile { get; set; }

    [JsonPropertyName("chatbotFeatures")]
    public required ChatbotFeatures ChatbotFeatures { get; set; }

    /// <summary>
    ///     Keyed to profile type e.g. "Standard" or "SPT Developer"
    /// </summary>
    [JsonPropertyName("createNewProfileTypesBlacklist")]
    public required HashSet<string> CreateNewProfileTypesBlacklist { get; set; }

    /// <summary>
    ///     Profile ids to ignore when calculating achievement stats
    /// </summary>
    [JsonPropertyName("achievementProfileIdBlacklist")]
    public required HashSet<string>? AchievementProfileIdBlacklist { get; set; }

    /// <summary>
    ///     Allow profile registration through the launcher API. Set to false to require web registration.
    /// </summary>
    [JsonPropertyName("allowRegistration")]
    public bool AllowRegistration { get; set; }

    /// <summary>
    ///     Auto-repair profile item damage (duplicate/empty ids, orphaned parents, stale references)
    ///     on startup, pre-save and profile-list. See ProfileAutoRepairService.
    /// </summary>
    [JsonPropertyName("autoRepairProfiles")]
    public bool AutoRepairProfiles { get; set; } = true;

    /// <summary>
    ///     Lazy profile loading: scan only profile headers at startup, materialize full
    ///     profiles on demand. High-value for servers with many profiles. Default on.
    /// </summary>
    [JsonPropertyName("lazyProfileLoad")]
    public bool LazyProfileLoad { get; set; } = true;

    /// <summary>
    ///     Cache flea/trader read-only endpoint responses (final HTTP body) with
    ///     single-flight coalescing; event-driven invalidation + 60s TTL backstop.
    ///     Default on. See FleaTraderCacheService.
    /// </summary>
    [JsonPropertyName("fleaTraderCache")]
    public bool FleaTraderCache { get; set; } = true;

    /// <summary>
    ///     Flea bucket TTL backstop for FleaTraderCacheService (seconds). Offer-pool changes
    ///     are invalidated by events; this longer TTL only covers missed hooks and extends
    ///     cross-session hits on the global price endpoints. Default 300.
    /// </summary>
    [JsonPropertyName("fleaTraderCacheTtlSeconds")]
    public int FleaTraderCacheTtlSeconds { get; set; } = 300;

    /// <summary>
    ///     Generate bot waves serially instead of in parallel. Lowers memory spikes and
    ///     thread contention during generation at the cost of longer generation time.
    ///     Default off (upstream parallel behaviour).
    /// </summary>
    [JsonPropertyName("botSerialGeneration")]
    public bool BotSerialGeneration { get; set; }
}

public record ChatbotFeatures
{
    [JsonPropertyName("sptFriendGiftsEnabled")]
    public bool SptFriendGiftsEnabled { get; set; }

    [JsonPropertyName("commandoFeatures")]
    public required CommandoFeatures CommandoFeatures { get; set; }

    [JsonPropertyName("commandUseLimits")]
    public required Dictionary<string, int?> CommandUseLimits { get; set; }

    /// <summary>
    ///     Human readable id to guid for each bot
    /// </summary>
    [JsonPropertyName("ids")]
    public required Dictionary<string, MongoId> Ids { get; set; }

    /// <summary>
    ///     Bot Ids player is allowed to interact with
    /// </summary>
    [JsonPropertyName("enabledBots")]
    public required Dictionary<MongoId, bool> EnabledBots { get; set; }
}

public record CommandoFeatures
{
    [JsonPropertyName("giveCommandEnabled")]
    public bool GiveCommandEnabled { get; set; }
}

/// <summary>
/// SMTP 邮件配置
/// </summary>
public record SmtpConfig
{
    /// <summary>
    /// SMTP 服务器地址
    /// </summary>
    [JsonPropertyName("server")]
    public required string Server { get; set; }

    /// <summary>
    /// SMTP 服务器端口
    /// </summary>
    [JsonPropertyName("port")]
    public required int Port { get; set; }

    /// <summary>
    /// 是否使用 SSL
    /// </summary>
    [JsonPropertyName("useSsl")]
    public required bool UseSsl { get; set; }

    /// <summary>
    /// SMTP 用户名
    /// </summary>
    [JsonPropertyName("username")]
    public required string Username { get; set; }

    /// <summary>
    /// SMTP 密码
    /// </summary>
    [JsonPropertyName("password")]
    public required string Password { get; set; }

    /// <summary>
    /// 发件人邮箱地址
    /// </summary>
    [JsonPropertyName("senderEmail")]
    public required string SenderEmail { get; set; }
}
