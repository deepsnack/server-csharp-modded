using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Launcher;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Services.Mod;
using SPTarkov.Server.Core.Utils;
using Info = SPTarkov.Server.Core.Models.Eft.Profile.Info;

namespace SPTarkov.Server.Core.Controllers;

[Injectable]
public class LauncherController(
    IReadOnlyList<SptMod> loadedMods,
    HashUtil hashUtil,
    SaveServer saveServer,
    HttpServerHelper httpServerHelper,
    ProfileHelper profileHelper,
    DatabaseService databaseService,
    ServerLocalisationService serverLocalisationService,
    ProfileDataService profileDataService,
    ConfigServer configServer,
    LastLoginService lastLoginService,
    PasswordStoreService passwordStoreService
)
{
    protected readonly CoreConfig CoreConfig = configServer.GetConfig<CoreConfig>();

    /// <summary>
    ///     Handle launcher connecting to server
    /// </summary>
    /// <returns>ConnectResponse</returns>
    public ConnectResponse Connect()
    {
        // Get all possible profile types + filter out any that are blacklisted
        var profileTemplates = databaseService
            .GetProfileTemplates()
            .Where(profile => !CoreConfig.Features.CreateNewProfileTypesBlacklist.Contains(profile.Key))
            .ToDictionary();

        return new ConnectResponse
        {
            BackendUrl = httpServerHelper.GetBackendUrl(),
            Name = CoreConfig.ServerName,
            Editions = profileTemplates.Select(x => x.Key).ToList(),
            ProfileDescriptions = GetProfileDescriptions(profileTemplates),
        };
    }

    /// <summary>
    ///     Get descriptive text for each of the profile editions a player can choose, keyed by profile.json profile type e.g. "Edge Of Darkness"
    /// </summary>
    /// <param name="profileTemplates">Profiles to get descriptions of</param>
    /// <returns>Dictionary of profile types with related descriptive text</returns>
    protected Dictionary<string, string> GetProfileDescriptions(Dictionary<string, ProfileSides> profileTemplates)
    {
        var result = new Dictionary<string, string>();
        foreach (var (profileKey, profile) in profileTemplates)
        {
            result.TryAdd(profileKey, serverLocalisationService.GetText(profile.DescriptionLocaleKey));
        }

        return result;
    }

    /// <summary>
    /// Get account info by session id without re-verifying password
    /// </summary>
    /// <param name="sessionId">Session/Player id</param>
    /// <returns>Account information</returns>
    public Info? Find(MongoId sessionId)
    {
        return saveServer.GetProfileInfoBySessionId(sessionId);
    }

    /// <summary>
    /// </summary>
    /// <param name="info"></param>
    /// <returns></returns>
    public async Task<MongoId> LoginAsync(LoginRequestData? info)
    {
        MongoId result = MongoId.Empty();

        // 用户名→sessionId 走索引（懒加载时只物化该档，不触发全量加载）
        var matchedId = string.IsNullOrEmpty(info?.Username) ? null : saveServer.GetSessionIdByUsername(info!.Username!);
        if (matchedId.HasValue)
        {
            var sessionId = matchedId.Value;
            var account = saveServer.GetProfile(sessionId).ProfileInfo;

            var inputPassword = info?.Password ?? string.Empty;

            // wipe=true 的旧档可能跳过 profile migration，在首次登录时补迁移并清理旧字段。
            if (!string.IsNullOrEmpty(account?.Password))
            {
                if (!passwordStoreService.ImportLegacyHash(sessionId, account.Password))
                {
                    return MongoId.Empty();
                }

                account.Password = null;
                await saveServer.SaveProfileAsync(sessionId);
            }

            var storedPassword = passwordStoreService.GetHash(sessionId);
            if (storedPassword is null)
            {
                if (!string.IsNullOrEmpty(inputPassword) && !passwordStoreService.SetPassword(sessionId, inputPassword))
                {
                    return MongoId.Empty();
                }

                result = sessionId;
            }
            else if (passwordStoreService.Verify(sessionId, inputPassword))
            {
                // 存储的密码不为空，验证密码正确性
                result = sessionId;
            }
        }

        // 登录成功记录时间戳，供离线存档清理判断活跃度（原 RecordLastLoginPatch 内联）
        if (!result.IsEmpty)
        {
            lastLoginService.Record(result);
        }

        return result;
    }

    /// <summary>
    /// </summary>
    /// <param name="info"></param>
    /// <returns></returns>
    public async Task<MongoId> Register(RegisterData info)
    {
        if (!CoreConfig.Features.AllowRegistration)
        {
            return MongoId.Empty();
        }

        if (saveServer.GetSessionIdByUsername(info.Username) is not null)
        {
            return MongoId.Empty();
        }

        return await CreateAccount(info);
    }

    /// <summary>
    /// </summary>
    /// <param name="info"></param>
    /// <returns></returns>
    protected async Task<MongoId> CreateAccount(RegisterData info)
    {
        var profileId = new MongoId();
        var scavId = new MongoId();

        var newProfileDetails = new Info
        {
            ProfileId = profileId,
            ScavengerId = scavId,
            Aid = hashUtil.GenerateAccountId(),
            Username = info.Username,
            IsWiped = true,
            Edition = info.Edition,
        };
        saveServer.CreateProfile(newProfileDetails);

        if (!passwordStoreService.SetPassword(profileId, info.Password))
        {
            saveServer.RemoveProfile(profileId, force: true);
            return MongoId.Empty();
        }

        await saveServer.LoadProfileAsync(profileId);
        await saveServer.SaveProfileAsync(profileId);

        return profileId;
    }

    /// <summary>
    /// </summary>
    /// <param name="info"></param>
    /// <returns></returns>
    public async Task<MongoId> ChangeUsernameAsync(ChangeRequestData info)
    {
        var sessionID = await LoginAsync(info);

        if (!sessionID.IsEmpty)
        {
            saveServer.GetProfile(sessionID).ProfileInfo!.Username = info.Change;
            saveServer.MarkProfileDirty(sessionID);
        }

        return sessionID;
    }

    public async Task<MongoId> ChangePasswordAsync(ChangeRequestData info)
    {
        var sessionId = await LoginAsync(info);

        if (!sessionId.IsEmpty)
        {
            if (!passwordStoreService.SetPassword(sessionId, info.Change))
            {
                return MongoId.Empty();
            }
        }

        return sessionId;
    }

    /// <summary>
    ///     Handle launcher requesting profile be wiped
    /// </summary>
    /// <param name="info">Registration data</param>
    /// <returns>Session id</returns>
    public async Task<MongoId> WipeAsync(RegisterData info)
    {
        if (!CoreConfig.AllowProfileWipe)
        {
            return MongoId.Empty();
        }

        var sessionId = await LoginAsync(info);

        if (!sessionId.IsEmpty)
        {
            var profileInfo = saveServer.GetProfile(sessionId).ProfileInfo;
            profileInfo!.Edition = info.Edition;
            profileInfo.IsWiped = true;

            // Clear any data modders may have stored
            profileDataService.ClearProfileData(sessionId);
            saveServer.MarkProfileDirty(sessionId);
            await saveServer.SaveAsync();
        }

        return sessionId;
    }

    /// <summary>
    /// </summary>
    /// <returns></returns>
    public string GetCompatibleTarkovVersion()
    {
        return CoreConfig.CompatibleTarkovVersion;
    }

    /// <summary>
    ///     Get the mods the server has currently loaded
    /// </summary>
    /// <returns>Dictionary of mod name and mod details</returns>
    public Dictionary<string, AbstractModMetadata> GetLoadedServerMods()
    {
        return loadedMods.ToDictionary(sptMod => sptMod.ModMetadata?.Name ?? "UNKNOWN MOD", sptMod => sptMod.ModMetadata);
    }

    /// <summary>
    ///     Get the WebDAV game-update source config the launcher uses to list/download update packages.
    ///     Read from SPT_Data/webdav/config.json (template written on first run). An empty url means
    ///     "not configured" — a valid state the launcher handles gracefully.
    /// </summary>
    /// <returns>WebDAV config (url/username/password/zipDirectory)</returns>
    public WebDavModConfig GetWebDavConfig()
    {
        return WebDavModConfig.Load();
    }

    /// <summary>
    ///     Get the mods a profile has ever loaded into game with
    /// </summary>
    /// <param name="sessionId">Session/Player id</param>
    /// <returns>Array of mod details</returns>
    public List<ModDetails> GetServerModsProfileUsed(MongoId sessionId)
    {
        var profile = profileHelper.GetFullProfile(sessionId);

        if (profile?.SptData?.Mods is not null)
        {
            return GetProfileModsGroupedByModName(profile?.SptData?.Mods);
        }

        return [];
    }

    /// <summary>
    /// Group all mods used by profile grouped by mod name
    /// </summary>
    /// <param name="profileMods"></param>
    /// <returns></returns>
    public List<ModDetails> GetProfileModsGroupedByModName(List<ModDetails> profileMods)
    {
        // Group all mods used by profile by name
        var modsGroupedByName = new Dictionary<string, List<ModDetails>>();
        foreach (var mod in profileMods)
        {
            if (!modsGroupedByName.ContainsKey(mod.Name))
            {
                modsGroupedByName[mod.Name] = [];
            }

            modsGroupedByName[mod.Name].Add(mod);
        }

        // Find the highest versioned mod and add to results array
        var result = new List<ModDetails>();
        foreach (var (modName, modDatas) in modsGroupedByName)
        {
            var chosenVersion = modDatas.FirstOrDefault(x => x.Name == modName); // && x.Version == highestVersion
            if (chosenVersion is null)
            {
                continue;
            }

            result.Add(chosenVersion);
        }

        return result;
    }
}
