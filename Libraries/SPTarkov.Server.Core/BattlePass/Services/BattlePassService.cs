using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>
///     通行证进度核心：登录校验、经验/等级换算、领奖。等级独立于 PMC 等级，存于 BP 自有进度文件。
/// </summary>
[Injectable]
public class BattlePassService(
    SaveServer saveServer,
    BattlePassRewardService rewardService,
    ProfileHelper profileHelper,
    ISptLogger<BattlePassService> logger
)
{
    /// <summary>用用户名+密码校验，成功返回 profileId，失败返回 null。复用存档 Info.Password（A1 树内统一存储，SHA256）。</summary>
    public string? VerifyLogin(string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        var profileId = ResolveProfileIdByUsername(username);
        if (profileId is null)
        {
            return null;
        }

        var stored = saveServer.GetProfiles().TryGetValue(new MongoId(profileId), out var authProfile)
            ? authProfile.ProfileInfo?.Password
            : null;

        // 老存档可能尚未设密码：无密码记录则拒绝网页登录（网页登录要求已设密码）。
        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }

        return stored == EncryptPassword(password ?? string.Empty) ? profileId : null;
    }

    /// <summary>SHA256 → 大写无分隔十六进制，与 LauncherController.EncryptPassword 同源算法。</summary>
    private static string EncryptPassword(string password)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(password)));
    }

    /// <summary>按用户名（忽略大小写）在已加载档案中查 profileId。</summary>
    public string? ResolveProfileIdByUsername(string username)
    {
        foreach (var (id, profile) in saveServer.GetProfiles())
        {
            var uname = profile.ProfileInfo?.Username;
            if (!string.IsNullOrEmpty(uname) && string.Equals(uname, username, StringComparison.OrdinalIgnoreCase))
            {
                return id.ToString();
            }
        }

        return null;
    }

    public string? GetUsername(string profileId)
    {
        foreach (var (id, profile) in saveServer.GetProfiles())
        {
            if (id.ToString() == profileId)
            {
                return profile.ProfileInfo?.Username;
            }
        }

        return null;
    }

    /// <summary>确保进度对应当前赛季；跨赛季则重置（保留壳，归档留待后续增强）。</summary>
    public BpProgress GetOrResetProgress(string profileId, BpSeason season)
    {
        var prog = BattlePassStore.GetProgress(profileId);
        if (prog.SeasonId != season.SeasonId)
        {
            prog = new BpProgress { SeasonId = season.SeasonId };
            BattlePassStore.SaveProgress(profileId, prog);
        }

        return prog;
    }

    /// <summary>累加 BP 经验并按曲线进位等级（不超过 MaxLevel）。返回是否有等级变化。</summary>
    public bool AddXp(BpProgress prog, BpSeason season, int xp)
    {
        if (xp <= 0)
        {
            return false;
        }

        prog.Xp += xp;
        var leveled = false;

        while (prog.Level < season.MaxLevel)
        {
            var need = season.XpToReach(prog.Level + 1);
            if (need <= 0 || prog.Xp < need)
            {
                break;
            }

            prog.Xp -= need;
            prog.Level++;
            leveled = true;
        }

        // 满级后：经验灌入「循环奖励」轮次（每轮 CycleXpEffective），而非清零溢出
        if (prog.Level >= season.MaxLevel)
        {
            var cycleXp = season.CycleXpEffective;
            if (cycleXp > 0)
            {
                while (prog.Xp >= cycleXp)
                {
                    prog.Xp -= cycleXp;
                    prog.CyclesCompleted++;
                    leveled = true;
                }
            }
            else
            {
                prog.Xp = 0;
            }
        }

        return leveled;
    }

    /// <summary>直升若干等级（激活码 type=levels）。</summary>
    public void AddLevels(BpProgress prog, BpSeason season, int levels)
    {
        if (levels <= 0)
        {
            return;
        }

        prog.Level = Math.Min(season.MaxLevel, prog.Level + levels);
        prog.Xp = 0;
    }

    /// <summary>
    ///     领取某等级某轨奖励。返回 (成功, 消息)。
    ///     校验：等级达标 + 付费轨需已解锁 + 未重复领取。
    /// </summary>
    public (bool ok, string message) Claim(string profileId, BpProgress prog, BpSeason season, int level, string track)
    {
        if (level < 1 || level > season.MaxLevel || level > prog.Level)
        {
            return (false, "等级未达到");
        }

        var tracks = BattlePassStore.GetTracks();
        if (!tracks.TryGetValue(level, out var levelRewards))
        {
            return (false, "该等级无奖励");
        }

        var isPremium = string.Equals(track, "premium", StringComparison.OrdinalIgnoreCase);
        if (isPremium && !prog.PremiumUnlocked)
        {
            return (false, "付费轨未解锁");
        }

        var claimedSet = isPremium ? prog.ClaimedPremium : prog.ClaimedFree;
        if (claimedSet.Contains(level))
        {
            return (false, "已领取");
        }

        var rewards = isPremium ? levelRewards.Premium : levelRewards.Free;
        if (rewards.Count == 0)
        {
            claimedSet.Add(level); // 无奖励也标记，避免反复点击
            BattlePassStore.SaveProgress(profileId, prog);
            return (true, "该等级该轨无奖励");
        }

        var message = GrantRewardList(
            profileId,
            rewards,
            $"【通行证】赛季「{season.Name}」{(isPremium ? "付费" : "免费")}轨 {level} 级奖励，请查收。"
        );

        claimedSet.Add(level);
        BattlePassStore.SaveProgress(profileId, prog);
        return (true, message);
    }

    /// <summary>
    ///     领取满级后某一轮「循环奖励」。返回 (成功, 消息)。
    ///     校验：该轮已达成（cycle &lt;= CyclesCompleted）+ 付费轨需已解锁 + 未重复领取。
    ///     循环奖励配置存于 tracks 的保留键 0（<see cref="BattlePassStore.CycleRewardLevelKey"/>）。
    /// </summary>
    public (bool ok, string message) ClaimCycle(string profileId, BpProgress prog, BpSeason season, int cycle, string track)
    {
        if (cycle < 1 || cycle > prog.CyclesCompleted)
        {
            return (false, "该循环轮次未达成");
        }

        var tracks = BattlePassStore.GetTracks();
        if (!tracks.TryGetValue(BattlePassStore.CycleRewardLevelKey, out var cycleRewards))
        {
            return (false, "未配置循环奖励");
        }

        var isPremium = string.Equals(track, "premium", StringComparison.OrdinalIgnoreCase);
        if (isPremium && !prog.PremiumUnlocked)
        {
            return (false, "付费轨未解锁");
        }

        var claimedSet = isPremium ? prog.ClaimedCyclePremium : prog.ClaimedCycleFree;
        if (claimedSet.Contains(cycle))
        {
            return (false, "已领取");
        }

        var rewards = isPremium ? cycleRewards.Premium : cycleRewards.Free;
        if (rewards.Count == 0)
        {
            claimedSet.Add(cycle);
            BattlePassStore.SaveProgress(profileId, prog);
            return (true, "该轨循环奖励为空");
        }

        var message = GrantRewardList(
            profileId,
            rewards,
            $"【通行证】赛季「{season.Name}」{(isPremium ? "付费" : "免费")}轨 循环奖励第 {cycle} 轮，请查收。"
        );

        claimedSet.Add(cycle);
        BattlePassStore.SaveProgress(profileId, prog);
        return (true, message);
    }

    /// <summary>
    ///     发放一组奖励（按类型分流：item 邮寄；purchaseRight 解锁商人货架；recipe 解锁配方；title 解锁称号），
    ///     必要时持久化游戏档案。返回展示消息。等级奖励与循环奖励共用。
    /// </summary>
    private string GrantRewardList(string profileId, List<BpReward> rewards, string mailMessage)
    {
        var itemRewards = new List<BpReward>();
        var unlockedOffers = 0;
        var unlockedRecipes = 0;
        var unlockedTitles = 0;
        var profileTouched = false;

        foreach (var r in rewards)
        {
            switch ((r.Type ?? "item").Trim().ToLowerInvariant())
            {
                case "purchaseright":
                    if (!string.IsNullOrWhiteSpace(r.OfferId) && UnlockPurchaseRight(profileId, r.OfferId!))
                    {
                        unlockedOffers++;
                        profileTouched = true;
                    }
                    break;
                case "recipe":
                    if (!string.IsNullOrWhiteSpace(r.RecipeId) && UnlockRecipe(profileId, r.RecipeId!))
                    {
                        unlockedRecipes++;
                        profileTouched = true;
                    }
                    break;
                case "title":
                    // 称号存自有文件（titles/{profileId}.json），不动 EFT 档案，无需 PersistProfile
                    if (!string.IsNullOrWhiteSpace(r.TitleId) && BattlePassStore.GrantTitle(profileId, r.TitleId!))
                    {
                        unlockedTitles++;
                    }
                    break;
                default:
                    itemRewards.Add(r);
                    break;
            }
        }

        if (itemRewards.Count > 0)
        {
            rewardService.Deliver(profileId, itemRewards, mailMessage);
        }

        if (profileTouched)
        {
            PersistProfile(profileId);
        }

        var parts = new List<string>();
        if (itemRewards.Count > 0)
        {
            parts.Add("物品已发送至游戏内邮箱");
        }
        if (unlockedOffers > 0)
        {
            parts.Add($"已解锁 {unlockedOffers} 项通行证商人购买权限");
        }
        if (unlockedRecipes > 0)
        {
            parts.Add($"已解锁 {unlockedRecipes} 个藏身处配方");
        }
        if (unlockedTitles > 0)
        {
            parts.Add($"已解锁 {unlockedTitles} 个称号");
        }

        return parts.Count > 0 ? "领取成功：" + string.Join("；", parts) : "领取成功";
    }

    /// <summary>
    ///     解锁某货架 offer 的购买权限：在玩家档案把对应「解锁 quest」置 Success，
    ///     原生 questassort 据此对该玩家放出此商品。幂等（已解锁返回 false）。
    /// </summary>
    private bool UnlockPurchaseRight(string profileId, string offerId)
    {
        var pmc = profileHelper.GetPmcProfile(new MongoId(profileId));
        if (pmc is null)
        {
            return false;
        }

        var questId = BattlePassTraderSync.UnlockQuestId(offerId);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        pmc.Quests ??= new List<QuestStatus>();

        var existing = pmc.Quests.FirstOrDefault(q => q.QId == questId);
        if (existing is null)
        {
            pmc.Quests.Add(
                new QuestStatus
                {
                    QId = questId,
                    StartTime = now,
                    Status = QuestStatusEnum.Success,
                    StatusTimers = new Dictionary<QuestStatusEnum, double> { [QuestStatusEnum.Success] = now },
                    CompletedConditions = new List<string>(),
                }
            );
            return true;
        }

        if (existing.Status == QuestStatusEnum.Success)
        {
            return false; // 已解锁
        }

        existing.Status = QuestStatusEnum.Success;
        existing.StatusTimers ??= new Dictionary<QuestStatusEnum, double>();
        existing.StatusTimers[QuestStatusEnum.Success] = now;
        return true;
    }

    /// <summary>解锁某藏身处制造配方（直接写玩家档案 UnlockedProductionRecipe，不经商人）。</summary>
    private bool UnlockRecipe(string profileId, string recipeId)
    {
        var pmc = profileHelper.GetPmcProfile(new MongoId(profileId));
        if (pmc is null)
        {
            return false;
        }

        MongoId rid;
        try
        {
            rid = new MongoId(recipeId.Trim());
        }
        catch
        {
            return false;
        }

        pmc.UnlockedInfo ??= new UnlockedInfo();
        pmc.UnlockedInfo.UnlockedProductionRecipe ??= new HashSet<MongoId>();
        return pmc.UnlockedInfo.UnlockedProductionRecipe.Add(rid);
    }

    /// <summary>持久化游戏档案（领取购买权限/配方修改了 PMC 档案，需落盘以跨重启保留）。</summary>
    private void PersistProfile(string profileId)
    {
        try
        {
            saveServer.SaveProfileAsync(new MongoId(profileId)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.Warning($"[SPT-BattlePass] 保存档案失败（内存态已生效）profile={profileId}: {ex.Message}");
        }
    }
}
