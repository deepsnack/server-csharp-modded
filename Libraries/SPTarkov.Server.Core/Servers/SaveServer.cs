using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;
using LogLevel = SPTarkov.Server.Core.Models.Spt.Logging.LogLevel;

namespace SPTarkov.Server.Core.Servers;

[Injectable(InjectionType.Singleton)]
public class SaveServer(
    FileUtil fileUtil,
    IEnumerable<SaveLoadRouter> saveLoadRouters,
    JsonUtil jsonUtil,
    HashUtil hashUtil,
    ServerLocalisationService serverLocalisationService,
    ProfileValidatorService profileValidatorService,
    BackupService backupService,
    ProfileAutoRepairService profileAutoRepairService,
    SoftResetService softResetService,
    PasswordStoreService passwordStoreService,
    ISptLogger<SaveServer> logger,
    ConfigServer configServer
)
{
    protected const string profileFilepath = "user/profiles/";

    /// <summary>
    /// 根据SessionId获取用户名（懒加载时回退头索引，不触发物化）
    /// </summary>
    public string? GetUsernameBySessionId(MongoId sessionId)
    {
        if (profiles.TryGetValue(sessionId, out var profile) && profile.ProfileInfo != null)
        {
            return profile.ProfileInfo.Username;
        }

        return lazyHeaders.TryGetValue(sessionId, out var header) ? header.ProfileInfo.Username : null;
    }

    /// <summary>
    /// 根据SessionId获取 PMC 昵称（懒加载时回退头索引，不触发物化；日志身份后缀用）
    /// </summary>
    public string? GetPmcNicknameBySessionId(MongoId sessionId)
    {
        if (profiles.TryGetValue(sessionId, out var profile))
        {
            return profile.CharacterData?.PmcData?.Info?.Nickname;
        }

        return lazyHeaders.TryGetValue(sessionId, out var header) ? header.Nickname : null;
    }

    /// <summary>
    /// 根据用户名获取SessionId（懒加载时回退用户名索引，不触发物化）
    /// </summary>
    public MongoId? GetSessionIdByUsername(string username)
    {
        foreach (var kvp in profiles)
        {
            if (kvp.Value.ProfileInfo?.Username == username)
            {
                return kvp.Key;
            }
        }

        // 注意：不能写成 `cond ? lazyId : null`——MongoId 有 implicit operator MongoId(string)，
        // null 字面量会经 null→string→MongoId 转换链把整个三元表达式推断为非空 MongoId，
        // 未命中时也返回 HasValue=true 的值。必须用显式 if/return null。
        if (lazyUsernameIndex.TryGetValue(username, out var lazyId))
        {
            return lazyId;
        }

        return null;
    }

    /// <summary>
    /// 获取profile文件路径（优先使用用户名命名，如果没有用户名则使用MongoId）
    /// public：ProfileCleanupService 重复文件清理/活跃度判断需要解析当前命名（原 private）
    /// </summary>
    public string GetProfileFilePath(MongoId sessionId)
    {
        var username = GetUsernameBySessionId(sessionId);
        if (!string.IsNullOrEmpty(username) && !IsHeadlessUsername(username))
        {
            // 清理用户名中的非法文件名字符
            var safeUsername = SanitizeFileName(username);
            return Path.Combine(profileFilepath, $"{safeUsername}.json");
        }
        // headless 存档（Fika 自动生成，用户名固定前缀 headless_）必须继续以 MongoId(ProfileId) 命名：
        // Fika 无头子系统全程以 ProfileId 识别存档/生成启动脚本，且 IsHeadlessClient 用 ProfileId 鉴权。
        // 若按用户名命名，ProfileCleanupService.DedupeProfileFiles 会把 MongoId 命名文件当重复删除，
        // 导致无头客户端鉴权失配、识别不到存档（[Fika Headless Client] Invalid headless client ...）。
        return Path.Combine(profileFilepath, $"{sessionId}.json");
    }

    /// <summary>
    /// 判断用户名是否为 Fika headless 存档（自动生成，固定前缀 headless_）。
    /// headless 存档不参与用户名命名，始终以 MongoId 命名。
    /// </summary>
    public static bool IsHeadlessUsername(string? username)
    {
        return username is not null && username.StartsWith("headless_", StringComparison.Ordinal);
    }

    /// <summary>
    /// 清理文件名中的非法字符
    /// </summary>
    private string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new StringBuilder(fileName);
        foreach (var c in invalidChars)
        {
            sanitized.Replace(c, '_');
        }
        return sanitized.ToString();
    }

    // onLoad = require("../bindings/SaveLoad");
    [Obsolete("This will be removed in the next version of SPT")]
    protected readonly Dictionary<string, Func<SptProfile, SptProfile>> onBeforeSaveCallbacks = new();

    protected readonly ConcurrentDictionary<MongoId, SptProfile> profiles = new();
    protected readonly ConcurrentDictionary<MongoId, string> saveMd5 = new();
    protected readonly ConcurrentDictionary<MongoId, SemaphoreSlim> saveLocks = new();

    // ---- 懒加载（原 SPT-ProfileCore LazyProfile 内联；开关 CoreConfig.Features.LazyProfileLoad，默认关）----
    protected readonly ConcurrentDictionary<MongoId, LazyProfileHeader> lazyHeaders = new();
    protected readonly ConcurrentDictionary<string, MongoId> lazyUsernameIndex = new(StringComparer.Ordinal);
    protected readonly ConcurrentDictionary<MongoId, object> lazyLoadLocks = new();
    protected bool? lazyEnabled;

    /// <summary>懒加载是否开启（config 启动后不热切换）。</summary>
    public bool LazyEnabled => lazyEnabled ??= configServer.GetConfig<CoreConfig>().Features.LazyProfileLoad;

    /// <summary>
    ///     Add callback to occur prior to saving profile changes
    /// </summary>
    /// <param name="id"> ID for the save callback </param>
    /// <param name="callback"> Callback to execute prior to running SaveServer.saveProfile() </param>
    [Obsolete("This will be removed in the next version of SPT")]
    public void AddBeforeSaveCallback(string id, Func<SptProfile, SptProfile> callback)
    {
        onBeforeSaveCallbacks[id] = callback;
    }

    /// <summary>
    ///     Remove a callback from being executed prior to saving profile in SaveServer.saveProfile()
    /// </summary>
    /// <param name="id"> ID of Callback to remove </param>
    [Obsolete("This will be removed in the next version of SPT")]
    public void RemoveBeforeSaveCallback(string id)
    {
        onBeforeSaveCallbacks.Remove(id);
    }

    /// <summary>
    ///     Load all profiles in /user/profiles folder into memory (this.profiles)
    /// </summary>
    public async Task LoadAsync()
    {
        // get files to load
        if (!fileUtil.DirectoryExists(profileFilepath))
        {
            fileUtil.CreateDirectory(profileFilepath);
        }

        var files = fileUtil.GetFiles(profileFilepath).Where(item => fileUtil.GetFileExtension(item) == "json");

        // 懒加载：启动只扫描存档头建索引，不反序列化整档；按需物化（GetProfile/GetProfiles 触发）
        if (LazyEnabled)
        {
            var lazyStopwatch = Stopwatch.StartNew();
            var scanned = files.Count(ScanProfileHeader);
            lazyStopwatch.Stop();
            logger.Success($"[LazyProfile] 已扫描 {scanned} 个存档头（{lazyStopwatch.ElapsedMilliseconds}ms），整档按需加载");
            return;
        }

        // load profiles
        var stopwatch = Stopwatch.StartNew();
        foreach (var file in files)
        {
            // 支持MongoId格式和用户名格式的文件名
            var filename = Path.GetFileNameWithoutExtension(file);

            // 如果文件名是有效的MongoId
            if (MongoId.IsValidMongoId(filename))
            {
                await LoadProfileAsync(filename);
            }
            else
            {
                // 尝试作为用户名文件加载（文件名不是有效的MongoId）。
                // 必须传带扩展名的完整路径——原先传去扩展名的纯文件名导致反序列化
                // File.Exists 永远为 false，用户名命名的存档从未真正加载（靠重复的
                // 旧 MongoId 文件兜底；dedupe 删除旧文件后会在重启时丢加载）。
                await LoadProfileByUsernameAsync(file, filename);
            }
        }

        stopwatch.Stop();
        if (logger.IsLogEnabled(LogLevel.Debug))
        {
            logger.Debug($"{files.Count()} Profiles took: {stopwatch.ElapsedMilliseconds}ms to load.");
        }
    }

    /// <summary>
    /// 通过用户名加载profile文件
    /// </summary>
    private async Task LoadProfileByUsernameAsync(string filePath, string username)
    {
        try
        {
            var profile = jsonUtil.DeserializeFromFile<SptProfile>(filePath);
            
            // 从文件内容中获取MongoId
            // 检查ProfileId是否有值且不为空
            if (profile?.ProfileInfo?.ProfileId.HasValue == true && !profile.ProfileInfo.ProfileId.Value.IsEmpty)
            {
                // 从可空类型中获取非空的MongoId作为sessionId
                var sessionId = profile.ProfileInfo.ProfileId.Value;
                
                // 验证profile是否有效并执行数据迁移
                var jsonNode = JsonNode.Parse(jsonUtil.Serialize(profile));
                if (jsonNode is JsonObject jsonObj)
                {
                    var validatedProfile = profileValidatorService.MigrateAndValidateProfile(jsonObj);
                    if (validatedProfile.ProfileInfo?.InvalidOrUnloadableProfile ?? false)
                    {
                        logger.Warning($"Profile {username} has validation errors");
                        profile.ProfileInfo.InvalidOrUnloadableProfile = true;
                    }
                    // sessionId已经是非空MongoId，可以安全作为字典键
                    profiles[sessionId] = validatedProfile;
                }
                else
                {
                    // sessionId已经是非空MongoId，可以安全作为字典键
                    profiles[sessionId] = profile;
                }
                
                // Run callbacks
                foreach (var callback in saveLoadRouters)
                {
                    profiles[sessionId] = callback.HandleLoad(GetProfile(sessionId));
                }
                
                logger.Info($"Loaded profile: {username} (SessionId: {sessionId})");
            }
            else
            {
                logger.Warning($"Profile file {username}.json does not contain valid MongoId in info.id");
            }
        }
        catch (Exception ex)
        {
            logger.Error($"Failed to load profile {username}: {ex.Message}");
        }
    }

    /// <summary>
    ///     Save changes for each profile from memory into user/profiles json
    /// </summary>
    public async Task SaveAsync()
    {
        // Save every profile
        var totalTime = 0L;
        foreach (var sessionID in profiles)
        {
            totalTime += await SaveProfileAsync(sessionID.Key);
        }

        if (profiles.Count > 0 && logger.IsLogEnabled(LogLevel.Debug))
        {
            logger.Debug($"Saved {profiles.Count} profiles, took: {totalTime}ms");
        }
    }

    /// <summary>
    ///     Get a player profile from memory
    /// </summary>
    /// <param name="sessionId"> Session ID </param>
    /// <returns> SptProfile of the player </returns>
    /// <exception cref="Exception"> Thrown when sessionId is null / empty or no profiles with that ID are found </exception>
    public SptProfile GetProfile(MongoId sessionId)
    {
        if (sessionId.IsEmpty)
        {
            throw new Exception("session id provided was empty, did you restart the server while the game was running?");
        }

        // 懒加载：未加载且头索引存在则按需物化
        EnsureMaterialized(sessionId);

        if (profiles == null || profiles.IsEmpty)
        {
            throw new Exception($"no profiles found in saveServer with id: {sessionId}");
        }

        if (!profiles.TryGetValue(sessionId, out var sptProfile))
        {
            throw new Exception($"no profile found for sessionId: {sessionId}");
        }

        return sptProfile;
    }

    public bool ProfileExists(MongoId id)
    {
        // 懒加载：离线档（仅头索引在场）也算存在
        return profiles.ContainsKey(id) || lazyHeaders.ContainsKey(id);
    }

    /// <summary>
    ///     Gets all profiles from memory
    /// </summary>
    /// <returns> Dictionary of Profiles with their ID as Keys. </returns>
    public Dictionary<MongoId, SptProfile> GetProfiles()
    {
        // 懒加载：全量扫描语义要求物化全部已知存档（兜底，保证按用户名/pmcId 等遍历命中离线档）
        MaterializeAll();

        return profiles.ToDictionary();
    }

    /// <summary>
    ///     Delete a profile by id (Does not remove the profile file!)
    /// </summary>
    /// <param name="sessionID"> ID of profile to remove </param>
    /// <returns> True when deleted, false when profile not found </returns>
    public bool DeleteProfileById(MongoId sessionID)
    {
        if (profiles.ContainsKey(sessionID))
        {
            if (profiles.TryRemove(sessionID, out _))
            {
                UnregisterLazyHeader(sessionID);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Create a new profile in memory with empty pmc/scav objects
    /// </summary>
    /// <param name="profileInfo"> Basic profile data </param>
    /// <exception cref="Exception"> Thrown when profile already exists </exception>
    public void CreateProfile(Info profileInfo)
    {
        if (!profileInfo.ProfileId.HasValue)
        {
            // TODO: Localize me
            throw new Exception("Creating profile failed: profile has no sessionId");
        }

        if (profiles.ContainsKey(profileInfo.ProfileId.Value))
        {
            // TODO: Localize me
            throw new Exception($"Creating profile failed: profile already exists for sessionId: {profileInfo.ProfileId}");
        }

        profiles.TryAdd(
            profileInfo.ProfileId.Value,
            new SptProfile
            {
                ProfileInfo = profileInfo,
                CharacterData = new Characters { PmcData = new PmcData(), ScavData = new PmcData() },
            }
        );

        // 懒加载：登记新档头索引
        if (profiles.TryGetValue(profileInfo.ProfileId.Value, out var createdProfile))
        {
            RegisterLazyHeader(createdProfile);
        }
    }

    /// <summary>
    ///     Add full profile in memory by key (info.id)
    /// </summary>
    /// <param name="profileDetails"> Profile to save </param>
    public void AddProfile(SptProfile profileDetails)
    {
        profiles.TryAdd(profileDetails.ProfileInfo!.ProfileId!.Value, profileDetails);

        // 软重置：角色重建时写回暂存的注册日期/在线时间（无暂存值时空操作）
        softResetService.ApplyPreservedStats(profileDetails);

        // 懒加载：登记/刷新头索引
        RegisterLazyHeader(profileDetails);
    }

    /// <summary>
    ///     Look up profile json in user/profiles by id and store in memory. <br />
    ///     Execute saveLoadRouters callbacks after being loaded into memory.
    /// </summary>
    /// <param name="sessionID"> ID of profile to store in memory </param>
    public async Task LoadProfileAsync(MongoId sessionID)
    {
        var filePath = Path.Combine(profileFilepath, $"{sessionID}.json");
        if (fileUtil.FileExists(filePath))
        // File found, store in profiles[]
        {
            JsonObject? profile = null;

            try
            {
                profile = await jsonUtil.DeserializeFromFileAsync<JsonObject>(filePath);
            }
            catch (JsonException e)
            {
                // If the profile fails to deserialize, it may have corrupted, try to restore from a backup
                logger.Warning($"Failed loading profile for {sessionID.ToString()}. Attempting to load backup");

                // We make a copy of the profile before overwriting it, just incase
                var corruptBackupPath = Path.Combine(profileFilepath, $"{sessionID}-corrupt.json");
                File.Copy(filePath, corruptBackupPath, true);

                if (backupService.RestoreProfile(sessionID))
                {
                    profile = await jsonUtil.DeserializeFromFileAsync<JsonObject>(filePath);
                    logger.Success("Profile restored from backup!");
                }
                else
                {
                    throw new Exception("Failed to restore profile backup", e);
                }
            }

            if (profile is not null)
            {
                try
                {
                    profiles[sessionID] = profileValidatorService.MigrateAndValidateProfile(profile);
                }
                catch (InvalidOperationException ex)
                {
                    logger.Critical($"Failed to load profile with ID '{sessionID}'");
                    logger.Critical(ex.ToString());
                }
            }
        }

        // We don't proceed further here as only one object in the profile has data in it.
        if (IsProfileInvalidOrUnloadable(sessionID))
        {
            return;
        }

        // Run callbacks
        foreach (var callback in saveLoadRouters) // HealthSaveLoadRouter, InraidSaveLoadRouter, InsuranceSaveLoadRouter, ProfileSaveLoadRouter. THESE SHOULD EXIST IN HERE
        {
            profiles[sessionID] = callback.HandleLoad(GetProfile(sessionID));
        }
    }

    /// <summary>
    ///     Save changes from in-memory profile to user/profiles json
    ///     Execute onBeforeSaveCallbacks callbacks prior to being saved to json
    /// </summary>
    /// <param name="sessionID"> Profile id (user/profiles/id.json) </param>
    /// <returns> Time taken to save the profile in seconds </returns>
    public async Task<long> SaveProfileAsync(MongoId sessionID)
    {
        // No need to save profiles that have been marked as invalid
        if (IsProfileInvalidOrUnloadable(sessionID))
        {
            return 0;
        }

        // Lock based on sessionID so we don't attempt to write to the same save file
        // multiple times at the same time, leading to file access contention
        SemaphoreSlim saveLock = saveLocks.GetOrAdd(sessionID, _ => new SemaphoreSlim(1, 1));
        await saveLock.WaitAsync();

        Stopwatch start;
        try
        {
            // 使用用户名作为文件名（如果可用；headless 存档强制 MongoId 命名）
            var filePath = GetProfileFilePath(sessionID);

            // 迁移：曾经按用户名命名的 headless 存档（headless_xxx.json）现改回 MongoId 命名，
            // 删除残留的旧用户名文件，避免重启时旧档与 MongoId 档双加载、陈旧数据回覆盖。
            var legacyUsername = GetUsernameBySessionId(sessionID);
            if (IsHeadlessUsername(legacyUsername))
            {
                var legacyPath = Path.Combine(profileFilepath, $"{SanitizeFileName(legacyUsername!)}.json");
                if (!string.Equals(Path.GetFullPath(legacyPath), Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase))
                {
                    fileUtil.DeleteFile(legacyPath);
                }
            }

            // Run pre-save callbacks before we save into json
            foreach (var callback in onBeforeSaveCallbacks)
            {
                var previous = profiles[sessionID];
                try
                {
                    profiles[sessionID] = onBeforeSaveCallbacks[callback.Key](profiles[sessionID]);
                }
                catch (Exception e)
                {
                    logger.Error(serverLocalisationService.GetText("profile_save_callback_error", new { callback, error = e }));
                    profiles[sessionID] = previous;
                }
            }

            // 存盘前自修复（开关在服务内部判断；原 ProfileAutoRepair pre-save 回调内联）
            profileAutoRepairService.RepairProfile(profiles[sessionID], sessionID, "pre-save");

            start = Stopwatch.StartNew();
            var jsonProfile = jsonUtil.Serialize(profiles[sessionID], !configServer.GetConfig<CoreConfig>().Features.CompressProfile);
            var fmd5 = await hashUtil.GenerateHashForDataAsync(HashingAlgorithm.MD5, jsonProfile);
            if (!saveMd5.TryGetValue(sessionID, out var currentMd5) || currentMd5 != fmd5)
            {
                saveMd5[sessionID] = fmd5;
                // save profile to disk
                await fileUtil.WriteFileAsync(filePath, jsonProfile);
            }

            start.Stop();
        }
        finally
        {
            saveLock.Release();
        }

        return start.ElapsedMilliseconds;
    }

    /// <summary>
    ///     Remove a physical profile json from user/profiles
    /// </summary>
    /// <param name="sessionID"> Profile ID to remove </param>
    /// <returns> True if successful </returns>
    /// <param name="sessionID"> Profile ID to remove </param>
    /// <param name="force"> true 时无视软重置开关执行真删除（管理员删号/半成品清理用） </param>
    public bool RemoveProfile(MongoId sessionID, bool force = false)
    {
        // 软重置（原 RemoveProfileSoftResetPatch 内联）：开启时把"删除存档"改为保留账号身份的进度擦除。
        // 与 mod 版不同：擦除后的档保留在内存（树内全量加载，无 LazyProfile header 可重注册），
        // 落盘走 SaveProfileAsync 的用户名命名路径。
        if (!force && softResetService.Enabled && profiles.TryGetValue(sessionID, out var profileToReset) && profileToReset.ProfileInfo is not null)
        {
            return SoftResetProfile(sessionID);
        }

        if (!passwordStoreService.Remove(sessionID))
        {
            logger.Error($"Unable to remove credentials for profile {sessionID}; profile deletion was cancelled");
            return false;
        }

        // 获取用户名（用于删除用户名命名的文件）
        var username = GetUsernameBySessionId(sessionID);
        
        // 尝试删除MongoId命名的文件
        var mongoIdFile = Path.Combine(profileFilepath, $"{sessionID}.json");
        fileUtil.DeleteFile(mongoIdFile);
        
        // 尝试删除用户名命名的文件
        if (!string.IsNullOrEmpty(username))
        {
            var safeUsername = SanitizeFileName(username);
            var usernameFile = Path.Combine(profileFilepath, $"{safeUsername}.json");
            fileUtil.DeleteFile(usernameFile);
        }

        if (profiles.ContainsKey(sessionID))
        {
            profiles.TryRemove(sessionID, out _);
        }

        // 懒加载：清理头索引
        UnregisterLazyHeader(sessionID);

        return true;
    }

    /// <summary>
    ///     软重置：保留账号身份（Info：用户名/密码/版本/邮箱映射均不动），仅擦除全部游戏进度并标记 IsWiped。
    ///     与 <see cref="RemoveProfile"/> 的软重置分支共用同一套编排，但本方法不受 SoftReset 开关约束——
    ///     供注册页玩家自助、启动器删档、管理员显式软重置调用；真删账号必须显式调用 RemoveProfile(force: true)。
    /// </summary>
    /// <param name="sessionID"> 目标存档 ID </param>
    /// <returns> true 表示已软重置；档不存在/无 Info 返回 false </returns>
    public bool SoftResetProfile(MongoId sessionID)
    {
        SptProfile profileToReset;
        try
        {
            // LazyProfile mode may only have the header indexed at this point; materialize before wiping.
            profileToReset = GetProfile(sessionID);
        }
        catch
        {
            return false;
        }

        if (profileToReset.ProfileInfo is null)
        {
            return false;
        }

        softResetService.CapturePreservedStats(profileToReset);
        softResetService.WipeProfileContent(profileToReset);
        profileToReset.ProfileInfo.IsWiped = true;
        SaveProfileAsync(sessionID).GetAwaiter().GetResult();
        logger.Warning($"[SoftReset] profile {sessionID} progress wiped (identity preserved)");
        return true;
    }

    /// <summary>
    /// Determines whether the specified profile is marked as invalid or cannot be loaded.
    /// </summary>
    /// <param name="sessionID">The ID of the profile to check.</param>
    /// <returns>
    /// <c>true</c> if the profile is invalid or unloadable; otherwise, <c>false</c>.
    /// </returns>
    public bool IsProfileInvalidOrUnloadable(MongoId sessionID)
    {
        if (
            profiles.TryGetValue(sessionID, out var profile)
            && profile.ProfileInfo!.InvalidOrUnloadableProfile is not null
            && profile.ProfileInfo!.InvalidOrUnloadableProfile!.Value
        )
        {
            return true;
        }

        return false;
    }

    // ==================== 懒加载核心（原 SPT-ProfileCore LazyProfile 内联） ====================

    /// <summary>头索引只读视图（离线清理等不需要整档的场景用，不触发物化）。</summary>
    public IReadOnlyDictionary<MongoId, LazyProfileHeader> GetLazyHeaders()
    {
        return lazyHeaders;
    }

    /// <summary>仅已加载 profile 的快照（不触发物化；懒加载感知的全量扫描方用）。</summary>
    public Dictionary<MongoId, SptProfile> GetLoadedProfilesSnapshot()
    {
        return profiles.ToDictionary();
    }

    /// <summary>启动时只抽取存档头字段建索引，不反序列化整档。</summary>
    protected bool ScanProfileHeader(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        if (string.Equals(name, "activeMods", StringComparison.OrdinalIgnoreCase) || name.EndsWith("-corrupt", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(filePath));
            var root = doc.RootElement;

            if (!root.TryGetProperty("info", out var infoEl))
            {
                return false;
            }

            var profileInfo = jsonUtil.Deserialize<Info>(infoEl.GetRawText());
            if (profileInfo?.ProfileId is null || profileInfo.ProfileId.Value.IsEmpty)
            {
                return false;
            }

            string? nickname = null;
            int? level = null;
            string? side = null;
            int? experience = null;
            MongoId? pmcId = null;
            var hasRagfairOffers = false;

            if (
                root.TryGetProperty("characters", out var charsEl)
                && charsEl.ValueKind == JsonValueKind.Object
                && charsEl.TryGetProperty("pmc", out var pmcEl)
                && pmcEl.ValueKind == JsonValueKind.Object
            )
            {
                // 跳蚤挂单存在性：启动恢复市场时只物化有挂单的档
                if (
                    pmcEl.TryGetProperty("RagfairInfo", out var ragfairEl)
                    && ragfairEl.ValueKind == JsonValueKind.Object
                    && ragfairEl.TryGetProperty("offers", out var offersEl)
                    && offersEl.ValueKind == JsonValueKind.Array
                )
                {
                    hasRagfairOffers = offersEl.GetArrayLength() > 0;
                }

                if (pmcEl.TryGetProperty("_id", out var pmcIdEl) && pmcIdEl.ValueKind == JsonValueKind.String)
                {
                    var idStr = pmcIdEl.GetString();
                    if (!string.IsNullOrEmpty(idStr) && MongoId.IsValidMongoId(idStr))
                    {
                        pmcId = new MongoId(idStr);
                    }
                }

                if (pmcEl.TryGetProperty("Info", out var pmcInfoEl) && pmcInfoEl.ValueKind == JsonValueKind.Object)
                {
                    if (pmcInfoEl.TryGetProperty("Nickname", out var nick) && nick.ValueKind == JsonValueKind.String)
                    {
                        nickname = nick.GetString();
                    }

                    if (pmcInfoEl.TryGetProperty("Level", out var lvl) && lvl.ValueKind == JsonValueKind.Number)
                    {
                        level = lvl.GetInt32();
                    }

                    if (pmcInfoEl.TryGetProperty("Side", out var sd) && sd.ValueKind == JsonValueKind.String)
                    {
                        side = sd.GetString();
                    }

                    if (pmcInfoEl.TryGetProperty("Experience", out var xp) && xp.ValueKind == JsonValueKind.Number)
                    {
                        experience = xp.GetInt32();
                    }
                }
            }

            var header = new LazyProfileHeader
            {
                ProfileInfo = profileInfo,
                Nickname = nickname,
                Level = level,
                Side = side,
                Experience = experience,
                PmcId = pmcId,
                HasRagfairOffers = hasRagfairOffers,
                FilePath = filePath,
                IsLoaded = false,
                IsInvalid = profileInfo.InvalidOrUnloadableProfile ?? false,
            };

            IndexLazyHeader(profileInfo.ProfileId.Value, header);
            return true;
        }
        catch (Exception ex)
        {
            logger.Warning($"[LazyProfile] 存档头扫描失败 {filePath}: {ex.Message}");
            return false;
        }
    }

    protected void IndexLazyHeader(MongoId sessionId, LazyProfileHeader header)
    {
        lazyHeaders[sessionId] = header;
        if (!string.IsNullOrEmpty(header.ProfileInfo.Username))
        {
            lazyUsernameIndex[header.ProfileInfo.Username] = sessionId;
        }
    }

    /// <summary>内存中已创建/加载的 profile 登记头索引（关懒加载时空操作，避免无谓开销）。</summary>
    protected void RegisterLazyHeader(SptProfile profile)
    {
        if (!LazyEnabled || profile.ProfileInfo?.ProfileId is null || profile.ProfileInfo.ProfileId.Value.IsEmpty)
        {
            return;
        }

        var sessionId = profile.ProfileInfo.ProfileId.Value;
        var pmcInfo = profile.CharacterData?.PmcData?.Info;

        IndexLazyHeader(
            sessionId,
            new LazyProfileHeader
            {
                ProfileInfo = profile.ProfileInfo,
                Nickname = pmcInfo?.Nickname,
                Level = pmcInfo?.Level,
                Side = pmcInfo?.Side,
                Experience = pmcInfo?.Experience,
                PmcId = profile.CharacterData?.PmcData?.Id,
                HasRagfairOffers = profile.CharacterData?.PmcData?.RagfairInfo?.Offers is { Count: > 0 },
                FilePath = GetProfileFilePath(sessionId),
                IsLoaded = true,
                IsInvalid = profile.ProfileInfo.InvalidOrUnloadableProfile ?? false,
            }
        );
    }

    protected void UnregisterLazyHeader(MongoId sessionId)
    {
        if (lazyHeaders.TryRemove(sessionId, out var header))
        {
            if (!string.IsNullOrEmpty(header.ProfileInfo.Username))
            {
                lazyUsernameIndex.TryRemove(header.ProfileInfo.Username, out _);
            }
        }

        lazyLoadLocks.TryRemove(sessionId, out _);
    }

    /// <summary>若未加载且头索引存在，则同步物化（双检锁防并发重复加载）。</summary>
    protected void EnsureMaterialized(MongoId sessionId)
    {
        if (!LazyEnabled || profiles.ContainsKey(sessionId) || !lazyHeaders.TryGetValue(sessionId, out var header))
        {
            return;
        }

        var gate = lazyLoadLocks.GetOrAdd(sessionId, _ => new object());
        lock (gate)
        {
            if (profiles.ContainsKey(sessionId))
            {
                return;
            }

            var filename = Path.GetFileNameWithoutExtension(header.FilePath);
            if (MongoId.IsValidMongoId(filename))
            {
                LoadProfileAsync(sessionId).GetAwaiter().GetResult();
            }
            else
            {
                // 用户名命名的存档：按完整路径加载
                LoadProfileByUsernameAsync(header.FilePath, filename).GetAwaiter().GetResult();
            }

            header.IsLoaded = true;
        }
    }

    /// <summary>物化全部已知存档（GetProfiles 全量扫描语义兜底）。</summary>
    protected void MaterializeAll()
    {
        if (!LazyEnabled)
        {
            return;
        }

        foreach (var id in lazyHeaders.Keys)
        {
            if (!profiles.ContainsKey(id))
            {
                EnsureMaterialized(id);
            }
        }
    }
}
