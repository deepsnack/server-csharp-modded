using System.Text.Json.Serialization;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;

namespace SPTarkov.Server.Core.Services;

/// <summary>
///     离线存档清理（原 SPT-ProfileCore ProfileCleanup 内置化）。
///     活跃度判断三级回退：LastLoginService → 文件创建时间；删除走 SaveServer.RemoveProfile（两种命名文件都删）。
///     ⚠️ 清理默认 Enabled=false——破坏性操作必须服主显式开启。配置文件路径与 mod 版一致，生产配置无缝沿用。
///     另含重复存档文件清理（dedupeProfileFiles，默认开）：按用户名存盘后残留的旧 MongoId 文件名存档，
///     在确认用户名文件存在的前提下删除同 id 的 MongoId 命名文件。
/// </summary>
[Injectable(TypePriority = OnLoadOrder.SaveCallbacks + 2)]
public class ProfileCleanupService(
    SaveServer saveServer,
    FileUtil fileUtil,
    JsonUtil jsonUtil,
    LastLoginService lastLoginService,
    ISptLogger<ProfileCleanupService> logger
) : IOnLoad, IOnUpdate
{
    protected const string ProfileDir = "user/profiles/";

    protected static readonly string ConfigPath = Path.Combine(
        Directory.GetCurrentDirectory(),
        "SPT_Data",
        "profilecleanup",
        "config.json"
    );

    protected ProfileCleanupConfig config = new();

    public Task OnLoad()
    {
        config = LoadConfig();

        if (config.DedupeProfileFiles)
        {
            DedupeProfileFiles();
        }

        logger.Success(
            $"[ProfileCleanup] loaded; enabled={config.Enabled} runOnStartup={config.RunOnStartup} runOnTimer={config.RunOnTimer}"
        );

        if (config.RunOnStartup)
        {
            RunCleanup();
        }

        return Task.CompletedTask;
    }

    public Task<bool> OnUpdate(long secondsSinceLastRun)
    {
        if (!config.Enabled || !config.RunOnTimer)
        {
            return Task.FromResult(false);
        }

        if (secondsSinceLastRun < config.CleanupCheckIntervalSeconds)
        {
            return Task.FromResult(false);
        }

        RunCleanup();
        return Task.FromResult(true);
    }

    protected void RunCleanup()
    {
        if (!config.Enabled)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var toDelete = new List<MongoId>();

        // 懒加载时用头索引判断活跃度（不物化整档）；否则用内存 profile 表
        var candidates = saveServer.LazyEnabled
            ? saveServer.GetLazyHeaders().Select(kv => (Id: kv.Key, kv.Value.ProfileInfo.Username, kv.Value.FilePath))
            : saveServer.GetProfiles().Select(kv => (Id: kv.Key, kv.Value.ProfileInfo?.Username, FilePath: (string?) null));

        foreach (var (sessionId, username, headerPath) in candidates)
        {
            var lastLogin = lastLoginService.Get(sessionId);

            if (lastLogin.HasValue)
            {
                if ((now - lastLogin.Value) / 86400.0 >= config.InactiveDaysThreshold)
                {
                    toDelete.Add(sessionId);
                }

                continue;
            }

            // 从未登录过 — 回退到存档文件创建时间
            var path = headerPath is not null && fileUtil.FileExists(headerPath) ? headerPath : ResolveProfileFilePath(sessionId, username);
            if (path is not null && (DateTime.UtcNow - File.GetCreationTimeUtc(path)).TotalDays >= config.NeverLoggedInDaysThreshold)
            {
                toDelete.Add(sessionId);
            }
        }

        foreach (var sessionId in toDelete)
        {
            var username = saveServer.GetUsernameBySessionId(sessionId) ?? sessionId.ToString();
            saveServer.RemoveProfile(sessionId);
            logger.Warning($"[ProfileCleanup] deleted inactive profile: {username} (sessionId: {sessionId})");
        }

        if (toDelete.Count > 0)
        {
            logger.Info($"[ProfileCleanup] cleanup complete: {toDelete.Count} profile(s) deleted.");
        }
    }

    /// <summary>
    ///     清理"按用户名存盘后残留的旧 MongoId 文件名存档"：仅当同 id 的用户名命名文件存在时
    ///     才删除 MongoId 命名文件（保守策略；内存中以加载序后置的用户名文件为准，数据无损失）。
    /// </summary>
    protected void DedupeProfileFiles()
    {
        var removed = 0;

        // 懒加载时用头索引（用户名/路径已在头中），不物化整档
        var entries = saveServer.LazyEnabled
            ? saveServer.GetLazyHeaders().Select(kv => (Id: kv.Key, kv.Value.ProfileInfo.Username))
            : saveServer.GetProfiles().Select(kv => (Id: kv.Key, kv.Value.ProfileInfo?.Username));

        foreach (var (sessionId, username) in entries)
        {
            if (string.IsNullOrEmpty(username))
            {
                continue;
            }

            // headless 存档（自动生成，固定 headless_ 前缀）永远以 MongoId(ProfileId) 命名，
            // 且 Fika 无头子系统以 ProfileId 识别存档/鉴权。绝不能把它的 MongoId 文件当成
            // 用户名命名文件的"重复"删除——一旦删除会导致无头鉴权失配、无头从 Fika Manager
            // 状态列表里消失（[Fika Headless Client] Invalid headless client ...）。
            if (username.StartsWith("headless_", StringComparison.Ordinal))
            {
                continue;
            }

            var usernamePath = saveServer.GetProfileFilePath(sessionId);
            var mongoIdPath = Path.Combine(ProfileDir, $"{sessionId}.json");

            if (
                !string.Equals(Path.GetFullPath(usernamePath), Path.GetFullPath(mongoIdPath), StringComparison.OrdinalIgnoreCase)
                && fileUtil.FileExists(usernamePath)
                && fileUtil.FileExists(mongoIdPath)
            )
            {
                fileUtil.DeleteFile(mongoIdPath);
                removed++;
                logger.Info($"[ProfileCleanup] removed stale duplicate profile file: {mongoIdPath} (kept {usernamePath})");
            }
        }

        if (removed > 0)
        {
            if (saveServer.LazyEnabled)
            {
                saveServer.RebuildLazyProfileHeaders();
            }

            logger.Success($"[ProfileCleanup] profile file dedupe: removed {removed} stale file(s).");
        }
    }

    protected string? ResolveProfileFilePath(MongoId sessionId, string? username)
    {
        var usernamePath = saveServer.GetProfileFilePath(sessionId);
        if (fileUtil.FileExists(usernamePath))
        {
            return usernamePath;
        }

        var mongoIdPath = Path.Combine(ProfileDir, $"{sessionId}.json");
        return fileUtil.FileExists(mongoIdPath) ? mongoIdPath : null;
    }

    protected ProfileCleanupConfig LoadConfig()
    {
        try
        {
            if (!fileUtil.FileExists(ConfigPath))
            {
                var template = new ProfileCleanupConfig();
                fileUtil.WriteFile(ConfigPath, jsonUtil.Serialize(template, true) ?? "{}");
                return template;
            }

            return jsonUtil.Deserialize<ProfileCleanupConfig>(fileUtil.ReadFile(ConfigPath)) ?? new ProfileCleanupConfig();
        }
        catch
        {
            return new ProfileCleanupConfig();
        }
    }
}

/// <summary>
///     离线存档清理配置（文件 SPT_Data/profilecleanup/config.json，与原 mod 版字段兼容）。
/// </summary>
public record ProfileCleanupConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    /// <summary>距上次登录多少天后删除（活跃判断）。</summary>
    [JsonPropertyName("inactiveDaysThreshold")]
    public int InactiveDaysThreshold { get; set; } = 30;

    /// <summary>从未登录过的存档，距文件创建多少天后删除。</summary>
    [JsonPropertyName("neverLoggedInDaysThreshold")]
    public int NeverLoggedInDaysThreshold { get; set; } = 7;

    [JsonPropertyName("runOnStartup")]
    public bool RunOnStartup { get; set; } = true;

    [JsonPropertyName("runOnTimer")]
    public bool RunOnTimer { get; set; } = false;

    [JsonPropertyName("cleanupCheckIntervalSeconds")]
    public int CleanupCheckIntervalSeconds { get; set; } = 86400;

    /// <summary>启动时清理重复存档文件（用户名命名与 MongoId 命名并存时删后者），默认开。</summary>
    [JsonPropertyName("dedupeProfileFiles")]
    public bool DedupeProfileFiles { get; set; } = true;
}
