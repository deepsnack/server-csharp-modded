using SPTarkov.DI.Annotations;
using System.Collections.Concurrent;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>
///     通行证进度核心：登录校验、经验/等级换算、领奖。等级独立于 PMC 等级，存于 BP 自有进度文件。
/// </summary>
[Injectable]
public class BattlePassService(
    SaveServer saveServer,
    BattlePassRewardService rewardService,
    ProfileHelper profileHelper,
    PasswordStoreService passwordStoreService,
    ISptLogger<BattlePassService> logger
)
{
    private readonly ConcurrentDictionary<string, object> compensationLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>用用户名+密码校验，成功返回 profileId，失败返回 null。</summary>
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

        var sessionId = new MongoId(profileId);
        if (passwordStoreService.GetHash(sessionId) is null)
        {
            return null;
        }

        return passwordStoreService.Verify(sessionId, password) ? profileId : null;
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
        if (!MongoId.IsValidMongoId(profileId))
        {
            return null;
        }

        var sessionId = new MongoId(profileId);
        if (saveServer.GetProfiles().TryGetValue(sessionId, out var profile))
        {
            return profile.ProfileInfo?.Username;
        }

        if (saveServer.LazyEnabled && saveServer.GetLazyHeaders().TryGetValue(sessionId, out var header))
        {
            return header.ProfileInfo.Username;
        }

        return null;
    }

    public string? GetNickname(string profileId)
    {
        if (!MongoId.IsValidMongoId(profileId))
        {
            return null;
        }

        var sessionId = new MongoId(profileId);
        return saveServer.GetProfiles().TryGetValue(sessionId, out var profile)
            ? profile.CharacterData?.PmcData?.Info?.Nickname
            : null;
    }

    /// <summary>Fika headless 自动档案不属于通行证管理范围。</summary>
    public bool IsHeadlessProfile(string profileId)
    {
        return IsHeadlessUsername(GetUsername(profileId));
    }

    internal static bool IsHeadlessUsername(string? username) =>
        username?.StartsWith("headless_", StringComparison.OrdinalIgnoreCase) == true;

    public List<string> ListProfileIds()
    {
        var ids = saveServer.GetProfiles().Keys.Select(id => id.ToString());
        if (saveServer.LazyEnabled)
        {
            ids = ids.Concat(saveServer.GetLazyHeaders().Keys.Select(id => id.ToString()));
        }

        return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    ///     按昵称（PMC 主昵称，忽略大小写）批量解析当前佩戴的称号——主菜单在线玩家列表用。
    ///     只遍历一次已加载档案；无对应档案/未佩戴的昵称对应值为 null。
    /// </summary>
    public Dictionary<string, BpTitleView?> GetEquippedTitlesByNickname(IEnumerable<string> nicknames)
    {
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in nicknames)
        {
            if (!string.IsNullOrWhiteSpace(n))
            {
                wanted.Add(n.Trim());
            }
        }

        var result = new Dictionary<string, BpTitleView?>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
        {
            return result;
        }

        foreach (var (id, profile) in saveServer.GetProfiles())
        {
            var nick = profile.CharacterData?.PmcData?.Info?.Nickname;
            if (string.IsNullOrEmpty(nick) || !wanted.Contains(nick) || result.ContainsKey(nick))
            {
                continue;
            }

            result[nick] = BattlePassTitleApi.GetEquippedTitle(id.ToString());
        }

        return result;
    }

    /// <summary>确保进度对应当前赛季；跨赛季则重置（保留壳，归档留待后续增强）。</summary>
    public BpProgress GetOrResetProgress(string profileId, BpSeason season)
    {
        var prog = BattlePassStore.GetProgress(profileId);
        if (prog.SeasonId != season.SeasonId)
        {
            prog = new BpProgress { SeasonId = season.SeasonId, RewardLedgerInitialized = true };
            BattlePassStore.SaveProgress(profileId, prog);
        }
        else if (!prog.RewardLedgerInitialized)
        {
            BattlePassRewardLedger.InitializeBaseline(prog, BattlePassStore.GetTracks());
            BattlePassStore.SaveProgress(profileId, prog);
        }

        if (BattlePassPurchaseRights.Migrate(prog, BattlePassStore.GetTracks()))
        {
            BattlePassStore.SaveProgress(profileId, prog);
        }

        return prog;
    }

    /// <summary>启动时迁移现有当前赛季进度，建立补偿基线并恢复旧版购买权。</summary>
    public void InitializeExistingRewardLedgers()
    {
        var season = BattlePassStore.GetSeason();
        var tracks = BattlePassStore.GetTracks();
        var initialized = 0;
        var migratedRights = 0;

        foreach (var profileId in BattlePassStore.ListProgressProfileIds())
        {
            var progress = BattlePassStore.GetProgress(profileId);
            if (progress.SeasonId != season.SeasonId)
            {
                continue;
            }

            var changed = false;
            if (!progress.RewardLedgerInitialized)
            {
                BattlePassRewardLedger.InitializeBaseline(progress, tracks);
                initialized++;
                changed = true;
            }

            if (BattlePassPurchaseRights.Migrate(progress, tracks))
            {
                migratedRights++;
                changed = true;
            }

            if (changed)
            {
                BattlePassStore.SaveProgress(profileId, progress);
            }
        }

        if (initialized > 0)
        {
            logger.Info($"[SPT-BattlePass] 已为 {initialized} 份现有进度建立奖励补偿基线");
        }
        if (migratedRights > 0)
        {
            logger.Info($"[SPT-BattlePass] 已为 {migratedRights} 份现有进度迁移持久化商人购买权");
        }
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
            BattlePassRewardLedger.RecordTrack(prog, level, isPremium, rewards);
            claimedSet.Add(level); // 无奖励也标记，避免反复点击
            BattlePassStore.SaveProgress(profileId, prog);
            return (true, "该等级该轨无奖励");
        }

        var message = GrantRewardList(
            profileId,
            prog,
            rewards,
            $"【通行证】赛季「{season.Name}」{(isPremium ? "付费" : "免费")}轨 {level} 级奖励，请查收。"
        );

        BattlePassRewardLedger.RecordTrack(prog, level, isPremium, rewards);
        claimedSet.Add(level);
        BattlePassStore.SaveProgress(profileId, prog);
        return (true, message);
    }

    /// <summary>返回所有已领取普通等级轨中，管理员后续追加且尚未补发的奖励项数量。</summary>
    public int GetPendingCompensationCount(BpProgress progress)
    {
        return BattlePassRewardLedger.GetPending(progress, BattlePassStore.GetTracks()).Count;
    }

    /// <summary>一次性补发所有已领取普通等级轨里的新增奖励；循环奖励不参与补偿。</summary>
    public (bool ok, string message, int granted) ClaimCompensation(string profileId, BpSeason season)
    {
        var claimLock = compensationLocks.GetOrAdd(profileId, _ => new object());
        lock (claimLock)
        {
            var progress = GetOrResetProgress(profileId, season);
            var pending = BattlePassRewardLedger.GetPending(progress, BattlePassStore.GetTracks());
            if (pending.Count == 0)
            {
                return (false, "当前没有可领取的新增补偿", 0);
            }

            var message = GrantRewardList(
                profileId,
                progress,
                pending.Select(x => x.Reward).ToList(),
                $"【通行证】赛季「{season.Name}」奖励轨新增补偿，请查收。"
            );

            BattlePassRewardLedger.RecordPending(progress, pending);
            BattlePassStore.SaveProgress(profileId, progress);
            return (true, $"已补领 {pending.Count} 项新增奖励。{message}", pending.Count);
        }
    }

    /// <summary>
    ///     收回玩家全部通行证商人购买权，并清理旧版遗留的虚拟任务状态。
    /// </summary>
    public int RevokePurchaseRights(string profileId, BpProgress progress)
    {
        BattlePassPurchaseRights.Migrate(progress, BattlePassStore.GetTracks());
        var offerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        offerIds.UnionWith(progress.PurchaseRights);
        foreach (var offer in BattlePassStore.GetOffers())
        {
            if (!string.IsNullOrWhiteSpace(offer.Id))
            {
                offerIds.Add(offer.Id.Trim());
            }
        }

        foreach (var levelRewards in BattlePassStore.GetTracks().Values)
        {
            foreach (var reward in levelRewards.Free.Concat(levelRewards.Premium))
            {
                if (string.Equals(reward.Type, "purchaseRight", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(reward.OfferId))
                {
                    offerIds.Add(reward.OfferId.Trim());
                }
            }
        }

        const string ledgerPrefix = "purchaseright:";
        foreach (var rewardKey in (progress.GrantedTrackRewards ?? new Dictionary<string, HashSet<string>>()).Values.SelectMany(x => x ?? []))
        {
            if (rewardKey.StartsWith(ledgerPrefix, StringComparison.OrdinalIgnoreCase) && rewardKey.Length > ledgerPrefix.Length)
            {
                offerIds.Add(rewardKey[ledgerPrefix.Length..]);
            }
        }

        var revoked = BattlePassPurchaseRights.RevokeAll(progress);
        BattlePassStore.SaveProgress(profileId, progress);

        if (!MongoId.IsValidMongoId(profileId))
        {
            return revoked;
        }

        var unlockQuestIds = offerIds.Select(BattlePassTraderSync.UnlockQuestId).ToHashSet();
        try
        {
            var pmc = profileHelper.GetPmcProfile(new MongoId(profileId));
            if (pmc?.Quests is null || unlockQuestIds.Count == 0)
            {
                return revoked;
            }

            var removedLegacyQuests = pmc.Quests.RemoveAll(q => unlockQuestIds.Contains(q.QId));
            if (removedLegacyQuests > 0)
            {
                PersistProfile(profileId);
            }

            return revoked;
        }
        catch (Exception ex)
        {
            logger.Warning($"[SPT-BattlePass] 无法加载档案，购买权未收回 profile={profileId}: {ex.Message}");
            return revoked;
        }
    }

    /// <summary>收回玩家全部可追溯的通行证配方解锁并持久化档案。</summary>
    public int RevokeRecipes(string profileId, BpProgress progress)
    {
        if (!MongoId.IsValidMongoId(profileId))
        {
            return 0;
        }

        var recipeIds = BattlePassStore
            .GetCustomRecipes()
            .Select(x => x.Id)
            .Where(MongoId.IsValidMongoId)
            .Select(x => new MongoId(x))
            .ToHashSet();

        foreach (var levelRewards in BattlePassStore.GetTracks().Values)
        {
            foreach (var reward in levelRewards.Free.Concat(levelRewards.Premium))
            {
                if (string.Equals(reward.Type, "recipe", StringComparison.OrdinalIgnoreCase)
                    && MongoId.IsValidMongoId(reward.RecipeId))
                {
                    recipeIds.Add(new MongoId(reward.RecipeId!));
                }
            }
        }

        const string ledgerPrefix = "recipe:";
        foreach (var rewardKey in (progress.GrantedTrackRewards ?? new Dictionary<string, HashSet<string>>()).Values.SelectMany(x => x ?? []))
        {
            if (rewardKey.StartsWith(ledgerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var recipeId = rewardKey[ledgerPrefix.Length..];
                if (MongoId.IsValidMongoId(recipeId))
                {
                    recipeIds.Add(new MongoId(recipeId));
                }
            }
        }

        HashSet<MongoId>? unlocked;
        try
        {
            unlocked = profileHelper
                .GetPmcProfile(new MongoId(profileId))
                ?.UnlockedInfo
                ?.UnlockedProductionRecipe;
        }
        catch (Exception ex)
        {
            logger.Warning($"[SPT-BattlePass] 无法加载档案，配方未收回 profile={profileId}: {ex.Message}");
            return 0;
        }

        if (unlocked is null || recipeIds.Count == 0)
        {
            return 0;
        }

        var revoked = unlocked.RemoveWhere(recipeIds.Contains);
        if (revoked > 0)
        {
            PersistProfile(profileId);
        }

        return revoked;
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
            prog,
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
    private string GrantRewardList(string profileId, BpProgress progress, List<BpReward> rewards, string mailMessage)
    {
        var itemRewards = new List<BpReward>();
        var unlockedOffers = 0;
        var unlockedRecipes = 0;
        var unlockedTitles = 0;
        var skippedOwnedEntitlements = 0;
        var profileTouched = false;

        foreach (var r in rewards)
        {
            switch ((r.Type ?? "item").Trim().ToLowerInvariant())
            {
                case "purchaseright":
                    if (!string.IsNullOrWhiteSpace(r.OfferId))
                    {
                        var offerId = r.OfferId.Trim();
                        if (BattlePassPurchaseRights.Has(progress, offerId))
                        {
                            skippedOwnedEntitlements++;
                        }
                        else if (BattlePassPurchaseRights.Grant(progress, offerId))
                        {
                            unlockedOffers++;
                        }
                    }
                    break;
                case "recipe":
                    if (!string.IsNullOrWhiteSpace(r.RecipeId))
                    {
                        var recipeId = r.RecipeId.Trim();
                        if (HasRecipe(profileId, recipeId))
                        {
                            skippedOwnedEntitlements++;
                        }
                        else if (UnlockRecipe(profileId, recipeId))
                        {
                            unlockedRecipes++;
                            profileTouched = true;
                        }
                    }
                    break;
                case "title":
                    // 称号存自有文件（titles/{profileId}.json），不动 EFT 档案，无需 PersistProfile
                    if (!string.IsNullOrWhiteSpace(r.TitleId))
                    {
                        var titleId = r.TitleId.Trim();
                        if (BattlePassStore.GetPlayerTitles(profileId).Owned.Contains(titleId))
                        {
                            skippedOwnedEntitlements++;
                        }
                        else if (BattlePassStore.GrantTitle(profileId, titleId))
                        {
                            unlockedTitles++;
                        }
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
        if (skippedOwnedEntitlements > 0)
        {
            parts.Add($"已跳过 {skippedOwnedEntitlements} 项已拥有权益");
        }

        return parts.Count > 0 ? "领取成功：" + string.Join("；", parts) : "领取成功";
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

    private bool HasRecipe(string profileId, string recipeId)
    {
        if (!MongoId.IsValidMongoId(recipeId))
        {
            return false;
        }

        var pmc = profileHelper.GetPmcProfile(new MongoId(profileId));
        return pmc?.UnlockedInfo?.UnlockedProductionRecipe?.Contains(new MongoId(recipeId)) == true;
    }

    /// <summary>持久化游戏档案（配方修改了 PMC 档案，需落盘以跨重启保留）。</summary>
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
