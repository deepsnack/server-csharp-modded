using System.Security.Cryptography;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.BattlePass.ItemControl;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Generators;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;

namespace SPTarkov.Server.Core.Services;

[Injectable]
public class CreateProfileService(
    ISptLogger<CreateProfileService> logger,
    TimeUtil timeUtil,
    DatabaseService databaseService,
    ServerLocalisationService serverLocalisationService,
    ProfileHelper profileHelper,
    ItemHelper itemHelper,
    TraderHelper traderHelper,
    QuestHelper questHelper,
    QuestRewardHelper questRewardHelper,
    PrestigeHelper prestigeHelper,
    RewardHelper rewardHelper,
    ProfileFixerService profileFixerService,
    SaveServer saveServer,
    EventOutputHolder eventOutputHolder,
    PlayerScavGenerator playerScavGenerator,
    ICloner cloner,
    MailSendService mailSendService,
    ItemAcquisitionMaskService acquisitionMask,
    EditionUpgradeService editionUpgradeService,
    SoftResetService softResetService
)
{
    public async ValueTask<string> CreateProfile(MongoId sessionId, ProfileCreateRequestData request)
    {
        var account = cloner.Clone(saveServer.GetProfile(sessionId));
        var rawEdition = account.ProfileInfo.Edition;
        var templateEdition = editionUpgradeService.ResolveEdition(rawEdition) ?? EditionUpgradeService.EditionLadder[0];
        var profileTemplate = profileHelper.GetProfileTemplateForSide(templateEdition, request.Side);

        if (profileTemplate?.Character is null)
        {
            throw new InvalidOperationException(
                $"Unable to find profile template for account edition: {rawEdition} (resolved: {templateEdition}) and side: {request.Side}"
            );
        }

        if (!string.Equals(rawEdition, templateEdition, StringComparison.Ordinal))
        {
            logger.Info($"Resolved account edition '{rawEdition}' to profile template '{templateEdition}' for {sessionId}");
        }

        var profileTemplateClone = cloner.Clone(profileTemplate);

        var pmcData = profileTemplateClone.Character;

        // 服务期屏蔽：从新档初始库存剔除被标记移除的物品（作用于克隆模板，不破坏 DB 模板）
        if (acquisitionMask.HasAny && pmcData.Inventory?.Items is not null)
        {
            pmcData.Inventory.Items.RemoveAll(item => acquisitionMask.IsStartInvRemoved(request.Side, item.Template));
        }

        // Delete existing profile
        DeleteProfileBySessionId(sessionId);
        // PMC
        pmcData.Id = account.ProfileInfo.ProfileId;
        pmcData.Aid = account.ProfileInfo.Aid;
        pmcData.Savage = account.ProfileInfo.ScavengerId;
        pmcData.SessionId = sessionId;
        pmcData.Info.Nickname = request.Nickname;
        pmcData.Info.LowerNickname = request.Nickname.ToLowerInvariant();
        pmcData.Info.RegistrationDate = (int)timeUtil.GetTimeStamp();
        pmcData.Customization.Voice = databaseService.GetCustomization()[request.VoiceId].Id;
        pmcData.Stats = profileHelper.GetDefaultCounters();
        pmcData.Info.NeedWipeOptions = [];
        pmcData.Customization.Head = request.HeadId;
        pmcData.Health.UpdateTime = timeUtil.GetTimeStamp();
        pmcData.Quests = [];
        pmcData.Hideout.Seed = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        pmcData.RepeatableQuests = [];
        pmcData.CarExtractCounts = [];
        pmcData.CoopExtractCounts = [];
        pmcData.Achievements = [];

        pmcData.WishList = new();
        pmcData.Variables = new();

        // Process handling if the account has been forced to wipe
        // BSG keeps both the achievements, prestige level and the total in-game time in a wipe
        var oldPmc = account.CharacterData?.PmcData;
        var oldScav = account.CharacterData?.ScavData;

        if (oldPmc?.Achievements is not null)
        {
            pmcData.Achievements = oldPmc.Achievements;
        }

        if (oldPmc?.Prestige is not null)
        {
            pmcData.Prestige = oldPmc.Prestige;
            pmcData.Info.PrestigeLevel = oldPmc.Info?.PrestigeLevel;
        }

        UpdateInventoryEquipmentId(pmcData);

        pmcData.UnlockedInfo ??= new UnlockedInfo { UnlockedProductionRecipe = [] };

        // Add required items to pmc stash
        AddMissingInternalContainersToProfile(pmcData);

        // Change item IDs to be unique
        itemHelper.ReplaceProfileInventoryIds(pmcData.Inventory);

        // Create profile
        var profileDetails = new SptProfile
        {
            ProfileInfo = account.ProfileInfo,
            CharacterData = new Characters { PmcData = pmcData, ScavData = new PmcData() },
            UserBuildData = profileTemplateClone.UserBuilds,
            DialogueRecords = profileTemplateClone.Dialogues,
            SptData = profileHelper.GetDefaultSptDataObject(),
            InraidData = new Inraid(),
            InsuranceList = [],
            BtrDeliveryList = [],
            TraderPurchases = [],
            FriendProfileIds = [],
            CustomisationUnlocks = [],
        };

        // Set old account in-game time data on wipe, if it exists to the pmc
        if (oldPmc?.Stats?.Eft is not null)
        {
            if (pmcData.Stats.Eft is not null)
            {
                pmcData.Stats.Eft.TotalInGameTime = oldPmc.Stats.Eft.TotalInGameTime;

                // Get the old profile's scav lifetime counter, if it exists
                var lifetimeCounter = oldPmc.Stats.Eft.OverallCounters?.Items?.FirstOrDefault(x =>
                    x.Key?.Contains("LifeTime") == true
                );

                if (lifetimeCounter is not null)
                {
                    // Set the old lifetime counter back, bsg seems to use this as well to keep track of the total amount of time played
                    profileDetails.CharacterData.PmcData.Stats.Eft.OverallCounters.Items.Add(lifetimeCounter);
                }
            }
        }

        profileDetails.AddCustomisationUnlocksToProfile();

        profileDetails.AddSuitsToProfile(profileTemplateClone.Suits);

        profileFixerService.CheckForAndFixPmcProfileIssues(profileDetails.CharacterData.PmcData);

        saveServer.AddProfile(profileDetails);

        if (profileDetails.CharacterData.PmcData.Achievements.Count > 0)
        {
            var achievementsDb = databaseService.GetTemplates().Achievements;
            var achievementRewardItemsToSend = new List<Item>();

            foreach (var (achievementId, _) in profileDetails.CharacterData.PmcData.Achievements)
            {
                var rewards = achievementsDb.FirstOrDefault(achievementDb => achievementDb.Id == achievementId)?.Rewards;

                if (rewards is null)
                {
                    continue;
                }

                achievementRewardItemsToSend.AddRange(
                    rewardHelper.ApplyRewards(
                        rewards,
                        CustomisationSource.ACHIEVEMENT,
                        profileDetails,
                        profileDetails.CharacterData.PmcData,
                        achievementId
                    )
                );
            }

            if (achievementRewardItemsToSend.Count > 0)
            {
                mailSendService.SendLocalisedSystemMessageToPlayer(
                    profileDetails.ProfileInfo.ProfileId.Value,
                    "670547bb5fa0b1a7c30d5836 0",
                    achievementRewardItemsToSend,
                    [],
                    31536000
                );
            }
        }

        // Process handling if the account is forced to prestige, or if the account currently has any pending prestiges
        if (request.SptForcePrestigeLevel is not null || account.SptData?.PendingPrestige is not null)
        {
            var pendingPrestige = account.SptData?.PendingPrestige ?? new PendingPrestige { PrestigeLevel = request.SptForcePrestigeLevel };

            prestigeHelper.ProcessPendingPrestige(account, profileDetails, pendingPrestige);
        }

        if (profileTemplateClone.Trader.SetQuestsAvailableForStart ?? false)
        {
            questHelper.AddAllQuestsToProfile(profileDetails.CharacterData.PmcData, [QuestStatusEnum.AvailableForStart]);
        }

        // Profile is flagged as wanting quests set to ready to hand in and collect rewards
        if (profileTemplateClone.Trader.SetQuestsAvailableForFinish ?? false)
        {
            questHelper.AddAllQuestsToProfile(
                profileDetails.CharacterData.PmcData,
                [QuestStatusEnum.AvailableForStart, QuestStatusEnum.Started, QuestStatusEnum.AvailableForFinish]
            );

            // Make unused response so applyQuestReward works
            var response = eventOutputHolder.GetOutput(sessionId);

            // Add rewards for starting quests to profile
            GivePlayerStartingQuestRewards(profileDetails, sessionId, response);
        }

        ResetAllTradersInProfile(sessionId);

        saveServer.GetProfile(sessionId).CharacterData.ScavData = playerScavGenerator.Generate(sessionId);
        softResetService.MergePreservedStatsIfPending(saveServer.GetProfile(sessionId));

        // Set old account in-game time data on wipe, if it exists to the scav
        if (oldScav?.Stats?.Eft is not null)
        {
            if (profileDetails.CharacterData.ScavData.Stats?.Eft is not null)
            {
                profileDetails.CharacterData.ScavData.Stats.Eft.TotalInGameTime = oldScav.Stats.Eft.TotalInGameTime;

                // Get the old profile's scav lifetime counter, if it exists
                var lifetimeCounter = oldScav.Stats.Eft.OverallCounters?.Items?.FirstOrDefault(x =>
                    x.Key?.Contains("LifeTime") == true
                );

                if (lifetimeCounter is not null)
                {
                    // Set the old lifetime counter back, bsg seems to use this as well to keep track of the total amount of time played
                    saveServer.GetProfile(sessionId).CharacterData.ScavData.Stats.Eft.OverallCounters.Items.Add(lifetimeCounter);
                }
            }
        }

        // Store minimal profile and reload it
        await saveServer.SaveProfileAsync(sessionId);
        await saveServer.LoadProfileAsync(sessionId);

        // Completed account creation
        saveServer.GetProfile(sessionId).ProfileInfo.IsWiped = false;
        await saveServer.SaveProfileAsync(sessionId);

        return pmcData.Id;
    }

    /// <summary>
    ///     Delete a profile
    /// </summary>
    /// <param name="sessionID"> ID of profile to delete </param>
    protected void DeleteProfileBySessionId(MongoId sessionID)
    {
        if (saveServer.GetProfiles().ContainsKey(sessionID))
        {
            saveServer.DeleteProfileById(sessionID);
        }
        else
        {
            logger.Warning(serverLocalisationService.GetText("profile-unable_to_find_profile_by_id_cannot_delete", sessionID));
        }
    }

    /// <summary>
    ///     Make profiles pmcData.Inventory.equipment unique
    /// </summary>
    /// <param name="pmcData"> Profile to update </param>
    protected void UpdateInventoryEquipmentId(PmcData pmcData)
    {
        var oldEquipmentId = pmcData.Inventory.Equipment;
        pmcData.Inventory.Equipment = new MongoId();

        foreach (var item in pmcData.Inventory.Items)
        {
            if (item.ParentId == oldEquipmentId)
            {
                item.ParentId = pmcData.Inventory.Equipment;
                continue;
            }

            if (item.Id == oldEquipmentId)
            {
                item.Id = pmcData.Inventory.Equipment.Value;
            }
        }
    }

    /// <summary>
    ///     For each trader reset their state to what a level 1 player would see
    /// </summary>
    /// <param name="sessionId"> Session ID of profile to reset </param>
    protected void ResetAllTradersInProfile(MongoId sessionId)
    {
        foreach (var traderId in databaseService.GetTraders().Keys)
        {
            traderHelper.ResetTrader(sessionId, traderId);
        }
    }

    /// <summary>
    ///     Ensure a profile has the necessary internal containers e.g. questRaidItems / sortingTable <br />
    ///     DOES NOT check that stash exists
    /// </summary>
    /// <param name="pmcData"> Profile to check </param>
    public void AddMissingInternalContainersToProfile(PmcData pmcData)
    {
        if (!pmcData.Inventory.Items.Any(item => item.Id == pmcData.Inventory.HideoutCustomizationStashId))
        {
            pmcData.Inventory.Items.Add(
                new Item { Id = pmcData.Inventory.HideoutCustomizationStashId.Value, Template = ItemTpl.HIDEOUTAREACONTAINER_CUSTOMIZATION }
            );
        }

        if (!pmcData.Inventory.Items.Any(item => item.Id == pmcData.Inventory.SortingTable))
        {
            pmcData.Inventory.Items.Add(
                new Item { Id = pmcData.Inventory.SortingTable.Value, Template = ItemTpl.SORTINGTABLE_SORTING_TABLE }
            );
        }

        if (!pmcData.Inventory.Items.Any(item => item.Id == pmcData.Inventory.QuestStashItems))
        {
            pmcData.Inventory.Items.Add(new Item { Id = pmcData.Inventory.QuestStashItems.Value, Template = ItemTpl.STASH_QUESTOFFLINE });
        }

        if (!pmcData.Inventory.Items.Any(item => item.Id == pmcData.Inventory.QuestRaidItems))
        {
            pmcData.Inventory.Items.Add(new Item { Id = pmcData.Inventory.QuestRaidItems.Value, Template = ItemTpl.STASH_QUESTRAID });
        }
    }

    /// <summary>
    ///     Iterate over all quests in player profile, inspect rewards for the quests current state (accepted/completed)
    ///     and send rewards to them in mail
    /// </summary>
    /// <param name="profileDetails"> Player profile </param>
    /// <param name="sessionID"> Session ID </param>
    /// <param name="response"> Event router response </param>
    protected void GivePlayerStartingQuestRewards(SptProfile profileDetails, MongoId sessionID, ItemEventRouterResponse response)
    {
        foreach (var quest in profileDetails.CharacterData.PmcData.Quests)
        {
            var questFromDb = questHelper.GetQuestFromDb(quest.QId, profileDetails.CharacterData.PmcData);

            // Get messageId of text to send to player as text message in game
            // Copy of code from QuestController.acceptQuest()
            var messageId = questHelper.GetMessageIdForQuestStart(questFromDb.StartedMessageText, questFromDb.Description);
            var itemRewards = questRewardHelper.ApplyQuestReward(
                profileDetails.CharacterData.PmcData,
                quest.QId,
                QuestStatusEnum.Started,
                sessionID,
                response
            );

            mailSendService.SendLocalisedNpcMessageToPlayer(
                sessionID,
                questFromDb.TraderId,
                MessageType.QuestStart,
                messageId,
                itemRewards,
                timeUtil.GetHoursAsSeconds(100)
            );
        }
    }
}
