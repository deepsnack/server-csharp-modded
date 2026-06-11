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
    ISptLogger<SaveServer> logger,
    ConfigServer configServer
)
{
    protected const string profileFilepath = "user/profiles/";

    /// <summary>
    /// 根据SessionId获取用户名
    /// </summary>
    public string? GetUsernameBySessionId(MongoId sessionId)
    {
        if (profiles.TryGetValue(sessionId, out var profile) && profile.ProfileInfo != null)
        {
            return profile.ProfileInfo.Username;
        }
        return null;
    }

    /// <summary>
    /// 根据用户名获取SessionId
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
        return null;
    }

    /// <summary>
    /// 获取profile文件路径（优先使用用户名命名，如果没有用户名则使用MongoId）
    /// public：ProfileCleanupService 重复文件清理/活跃度判断需要解析当前命名（原 private）
    /// </summary>
    public string GetProfileFilePath(MongoId sessionId)
    {
        var username = GetUsernameBySessionId(sessionId);
        if (!string.IsNullOrEmpty(username))
        {
            // 清理用户名中的非法文件名字符
            var safeUsername = SanitizeFileName(username);
            return Path.Combine(profileFilepath, $"{safeUsername}.json");
        }
        return Path.Combine(profileFilepath, $"{sessionId}.json");
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

        // load profiles
        var stopwatch = Stopwatch.StartNew();
        foreach (var file in files)
        {
            // 支持MongoId格式和用户名格式的文件名
            var filename = Path.GetFileNameWithoutExtension(file);
            var filePath = fileUtil.StripExtension(file);
            
            // 如果文件名是有效的MongoId
            if (MongoId.IsValidMongoId(filename))
            {
                await LoadProfileAsync(filePath);
            }
            else
            {
                // 尝试作为用户名文件加载（文件名不是有效的MongoId）
                await LoadProfileByUsernameAsync(filePath, filename);
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
        return profiles.ContainsKey(id);
    }

    /// <summary>
    ///     Gets all profiles from memory
    /// </summary>
    /// <returns> Dictionary of Profiles with their ID as Keys. </returns>
    public Dictionary<MongoId, SptProfile> GetProfiles()
    {
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
    }

    /// <summary>
    ///     Add full profile in memory by key (info.id)
    /// </summary>
    /// <param name="profileDetails"> Profile to save </param>
    public void AddProfile(SptProfile profileDetails)
    {
        profiles.TryAdd(profileDetails.ProfileInfo!.ProfileId!.Value, profileDetails);
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
            // 使用用户名作为文件名（如果可用）
            var filePath = GetProfileFilePath(sessionID);

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
    public bool RemoveProfile(MongoId sessionID)
    {
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
}
