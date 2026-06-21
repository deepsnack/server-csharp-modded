using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Launcher;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;
using Info = SPTarkov.Server.Core.Models.Eft.Profile.Info;

namespace SPTarkov.Server.Core.Controllers;

[Injectable]
public class LauncherV2Controller(
    IReadOnlyList<SptMod> loadedMods,
    HashUtil hashUtil,
    SaveServer saveServer,
    DatabaseService databaseService,
    ServerLocalisationService serverLocalisationService,
    ConfigServer configServer,
    Watermark watermark,
    ProfileController profileController,
    LastLoginService lastLoginService,
    PasswordStoreService passwordStoreService
)
{
    protected readonly CoreConfig CoreConfig = configServer.GetConfig<CoreConfig>();

    /// <summary>
    ///     Returns a simple string of Pong!
    /// </summary>
    /// <returns></returns>
    public string Ping()
    {
        return "Pong!";
    }

    /// <summary>
    ///     Returns all available profile types and descriptions for creation.
    ///     - This is also localised.
    /// </summary>
    /// <returns>dict of profile names + description</returns>
    public Dictionary<string, string> Types()
    {
        var result = new Dictionary<string, string>();
        var dbProfiles = databaseService.GetProfileTemplates();

        foreach (var (templateName, template) in dbProfiles)
        {
            result.TryAdd(templateName, serverLocalisationService.GetText(template.DescriptionLocaleKey));
        }

        return result;
    }

    /// <summary>
    ///     Checks if login details were correct.
    /// </summary>
    /// <param name="info"></param>
    /// <returns></returns>
    public bool Login(LoginRequestData info)
    {
        var sessionId = GetSessionId(info);

        return !sessionId.IsEmpty;
    }

    /// <summary>
    ///     Register a new profile.
    /// </summary>
    /// <param name="info"></param>
    /// <returns></returns>
    public async Task<bool> Register(RegisterData info)
    {
        if (!CoreConfig.Features.AllowRegistration)
            return false;

        foreach (var (_, profile) in saveServer.GetProfiles())
        {
            if (info.Username == profile.ProfileInfo!.Username)
            {
                return false;
            }
        }

        return !(await CreateAccount(info)).IsEmpty;
    }

    /// <summary>
    ///     Remove profile from server.
    /// </summary>
    /// <param name="info"></param>
    /// <returns></returns>
    public bool Remove(LoginRequestData info)
    {
        var sessionId = GetSessionId(info);

        return !sessionId.IsEmpty && saveServer.SoftResetProfile(sessionId);
    }

    /// <summary>
    ///     Gets the Servers SPT Version.
    ///     - "4.0.0"
    /// </summary>
    /// <returns></returns>
    public string SptVersion()
    {
        return watermark.GetVersionTag();
    }

    /// <summary>
    ///     Gets the compatible EFT Version.
    ///     - "0.14.9.31124"
    /// </summary>
    /// <returns></returns>
    public string EftVersion()
    {
        return CoreConfig.CompatibleTarkovVersion;
    }

    /// <summary>
    ///     Gets the Servers loaded mods.
    /// </summary>
    /// <returns></returns>
    public Dictionary<string, AbstractModMetadata> LoadedMods()
    {
        return loadedMods.ToDictionary(sptMod => sptMod.ModMetadata.Name, sptMod => sptMod.ModMetadata);
    }

    /// <summary>
    ///     Creates the account from provided details.
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

    protected MongoId GetSessionId(LoginRequestData info)
    {
        var result = MongoId.Empty();

        // 用户名→sessionId 走索引（懒加载时只物化该档，不触发全量加载）
        var matchedId = string.IsNullOrEmpty(info.Username) ? null : saveServer.GetSessionIdByUsername(info.Username!);
        if (matchedId.HasValue)
        {
            var sessionId = matchedId.Value;
            var profileInfo = saveServer.GetProfile(sessionId).ProfileInfo!;

            var inputPassword = info.Password ?? string.Empty;

            if (!string.IsNullOrEmpty(profileInfo.Password))
            {
                if (!passwordStoreService.ImportLegacyHash(sessionId, profileInfo.Password))
                {
                    return MongoId.Empty();
                }

                profileInfo.Password = null;
                saveServer.SaveProfileAsync(sessionId).GetAwaiter().GetResult();
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
                result = sessionId;
            }
        }

        // 登录成功记录时间戳，供离线存档清理判断活跃度（原 LauncherV2GetSessionIdRecordPatch 内联）
        if (!result.IsEmpty)
        {
            lastLoginService.Record(result);
        }

        return result;
    }

    public SptProfile GetProfile(MongoId sessionId)
    {
        return saveServer.GetProfile(sessionId);
    }

    public MiniProfile? GetMiniProfileFromUsername(LoginRequestData info)
    {
        return profileController.GetMiniProfile(GetSessionId(info));
    }

    public bool Wipe(RegisterData info)
    {
        if (!CoreConfig.AllowProfileWipe)
        {
            return false;
        }

        var sessionId = GetSessionId(info);
        if (sessionId.IsEmpty)
        {
            return false;
        }

        var profileInfo = saveServer.GetProfile(sessionId).ProfileInfo;
        profileInfo!.Edition = info.Edition;
        profileInfo.IsWiped = true;
        saveServer.SaveProfileAsync(sessionId).GetAwaiter().GetResult();
        return true;
    }
}
