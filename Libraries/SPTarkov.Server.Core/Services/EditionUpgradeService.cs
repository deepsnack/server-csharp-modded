using System.Text.Json;
using System.Text.Json.Serialization;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils.Cloners;

namespace SPTarkov.Server.Core.Services;

/// <summary>
///     版本升级（U1-U6）：单向升级 Standard → Left Behind → Prepare To Escape → Edge Of Darkness → Unheard。
///     差量算法按 _tpl × upd 子集做集合减法；捆绑物品（带 mod 的武器、装满弹的弹匣等）整组发送；
///     管理员可配置版本别名映射，未命中时拒绝升级。
///     非物品差异（hideoutAreaStashes / DogTag _tpl / TradersInfo 初始声望和等级）同步。
///     存储：SPT_Data/webregister/edition_aliases.json + edition_upgrade_config.json。
/// </summary>
[Injectable(InjectionType.Singleton)]
public class EditionUpgradeService(
    SaveServer saveServer,
    DatabaseService databaseService,
    MailSendService mailSendService,
    RegisterActivationCodeService activationLog,
    ICloner cloner,
    ISptLogger<EditionUpgradeService> logger
)
{
    /// <summary>从低到高的标准升级链。索引越大版本越高。</summary>
    public static readonly IReadOnlyList<string> EditionLadder = new[]
    {
        "Standard",
        "Left Behind",
        "Prepare To Escape",
        "Edge Of Darkness",
        "Unheard",
    };

    protected static readonly Dictionary<string, string> BuiltInAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["标准"] = "Standard",
        ["标准版"] = "Standard",
        ["留守"] = "Left Behind",
        ["留守版"] = "Left Behind",
        ["遗落"] = "Left Behind",
        ["遗落版"] = "Left Behind",
        ["准备逃离"] = "Prepare To Escape",
        ["准备逃离版"] = "Prepare To Escape",
        ["prepare for escape"] = "Prepare To Escape",
        ["黑边"] = "Edge Of Darkness",
        ["黑边版"] = "Edge Of Darkness",
        ["eod"] = "Edge Of Darkness",
        ["闻所未闻"] = "Unheard",
        ["闻所未闻版"] = "Unheard",
        ["unheard edition"] = "Unheard",
        ["the unheard edition"] = "Unheard",
    };

    protected static string AliasFilePath => System.IO.Path.Combine(
        Directory.GetCurrentDirectory(),
        "SPT_Data",
        "webregister",
        "edition_aliases.json"
    );

    protected static string UpgradeConfigFilePath => System.IO.Path.Combine(
        Directory.GetCurrentDirectory(),
        "SPT_Data",
        "webregister",
        "edition_upgrade_config.json"
    );

    protected readonly object gate = new();
    protected EditionAliasFile? aliases;
    protected EditionUpgradeConfig? upgradeConfig;

    // ---- 别名映射 ----

    public Dictionary<string, string> ListAliases()
    {
        lock (gate)
        {
            return new Dictionary<string, string>(Aliases.Map, StringComparer.OrdinalIgnoreCase);
        }
    }

    public bool SetAlias(string fromName, string toStandardEdition)
    {
        if (string.IsNullOrWhiteSpace(fromName))
        {
            return false;
        }

        if (!EditionLadder.Contains(toStandardEdition, StringComparer.Ordinal))
        {
            return false;
        }

        lock (gate)
        {
            Aliases.Map[fromName.Trim()] = toStandardEdition;
            SaveAliases();
            return true;
        }
    }

    public bool RemoveAlias(string fromName)
    {
        lock (gate)
        {
            var removed = Aliases.Map.Remove(fromName);
            if (removed)
            {
                SaveAliases();
            }

            return removed;
        }
    }

    /// <summary>
    /// 解析存档真实档位：先在标准链上找；找不到再走管理员别名表；最后使用内置兜底别名。
    /// </summary>
    public string? ResolveEdition(string? rawEdition)
    {
        if (string.IsNullOrWhiteSpace(rawEdition))
        {
            return null;
        }

        var normalizedRawEdition = rawEdition.Trim();
        var standardEdition = EditionLadder.FirstOrDefault(e => string.Equals(e, normalizedRawEdition, StringComparison.OrdinalIgnoreCase));
        if (standardEdition is not null)
        {
            return standardEdition;
        }

        lock (gate)
        {
            if (
                Aliases.Map.TryGetValue(normalizedRawEdition, out var mapped)
                && EditionLadder.FirstOrDefault(e => string.Equals(e, mapped, StringComparison.OrdinalIgnoreCase)) is { } mappedEdition
            )
            {
                return mappedEdition;
            }
        }

        if (BuiltInAliases.TryGetValue(normalizedRawEdition, out var builtInEdition))
        {
            return builtInEdition;
        }

        return null;
    }

    public int RankOf(string edition)
    {
        for (var i = 0; i < EditionLadder.Count; i++)
        {
            if (EditionLadder[i] == edition)
            {
                return i;
            }
        }

        return -1;
    }

    // ---- 邮件配置 ----

    public EditionUpgradeConfig GetConfig()
    {
        lock (gate)
        {
            return cloner.Clone(UpgradeConfig);
        }
    }

    public void SetConfig(EditionUpgradeConfig newConfig)
    {
        lock (gate)
        {
            UpgradeConfig.MailSubject = string.IsNullOrWhiteSpace(newConfig.MailSubject)
                ? UpgradeConfig.MailSubject
                : newConfig.MailSubject;
            UpgradeConfig.MailBodyTemplate = string.IsNullOrWhiteSpace(newConfig.MailBodyTemplate)
                ? UpgradeConfig.MailBodyTemplate
                : newConfig.MailBodyTemplate;
            UpgradeConfig.MailExpiryDays = newConfig.MailExpiryDays > 0 ? newConfig.MailExpiryDays : UpgradeConfig.MailExpiryDays;
            SaveUpgradeConfig();
        }
    }

    // ---- 预览与执行 ----

    /// <summary>
    /// 计算升级补齐预览：玩家从 currentEdition 升至 targetEdition 时缺少的物品/非物品差异。
    /// 仅当 fromRank &lt; toRank 且两端均在标准链上时返回成功。
    /// </summary>
    public UpgradePreview Preview(MongoId profileId, string targetEdition)
    {
        var profile = saveServer.GetProfile(profileId);
        if (profile?.ProfileInfo == null || profile.CharacterData?.PmcData == null)
        {
            return UpgradePreview.Failed("存档不存在或未初始化 PMC");
        }

        var rawEdition = profile.ProfileInfo.Edition;
        var currentEdition = ResolveEdition(rawEdition);
        if (currentEdition == null)
        {
            return UpgradePreview.Failed($"当前版本「{rawEdition}」不在标准链中且未配置别名映射，请先到 [版本别名] 配置后再升级");
        }

        var targetCanonical = ResolveEdition(targetEdition);
        if (targetCanonical == null || targetCanonical != targetEdition)
        {
            return UpgradePreview.Failed($"目标版本「{targetEdition}」不是合法的标准档位");
        }

        var fromRank = RankOf(currentEdition);
        var toRank = RankOf(targetEdition);
        if (toRank <= fromRank)
        {
            return UpgradePreview.Failed($"只能单向升级：当前 {currentEdition} 不低于目标 {targetEdition}");
        }

        var profiles = databaseService.GetProfileTemplates();
        if (!profiles.TryGetValue(currentEdition, out var sourceTemplate) || !profiles.TryGetValue(targetEdition, out var targetTemplate))
        {
            return UpgradePreview.Failed("profiles.json 缺少所需档位模板");
        }

        var side = profile.CharacterData.PmcData.Info?.Side;
        var (sourceSide, targetSide) = SelectSides(sourceTemplate, targetTemplate, side);
        if (sourceSide == null || targetSide == null)
        {
            return UpgradePreview.Failed($"模板缺少边「{side}」数据");
        }

        var preview = new UpgradePreview
        {
            Success = true,
            ProfileId = profileId.ToString(),
            FromEdition = currentEdition,
            ToEdition = targetEdition,
            RawEdition = rawEdition,
            Side = side,
        };

        // 物品差：目标模板独占的根容器/根物品 → 整组挂送
        var bundles = ComputeItemBundleDiff(sourceSide, targetSide, profile.CharacterData.PmcData);
        preview.ItemBundles = bundles;
        preview.ItemRootCount = bundles.Count;
        preview.ItemTotalCount = bundles.Sum(b => b.Items.Count);

        // 非物品差
        preview.HideoutStashAdditions = ComputeHideoutStashDiff(sourceSide, targetSide, profile.CharacterData.PmcData);
        preview.DogTagTemplateChange = ComputeDogTagDiff(sourceSide, targetSide, profile.CharacterData.PmcData);
        preview.TraderInfoUpgrades = ComputeTraderUpgrades(sourceSide, targetSide, profile.CharacterData.PmcData);

        return preview;
    }

    /// <summary>
    /// 实际执行升级：发送补齐邮件 + 同步非物品差异 + 写入审计日志。
    /// 不修改 profile.Info.Edition（按设计决策）。
    /// </summary>
    public UpgradeResult Execute(MongoId profileId, string targetEdition, string operatorName)
    {
        var preview = Preview(profileId, targetEdition);
        if (!preview.Success)
        {
            return new UpgradeResult { Success = false, Message = preview.Message };
        }

        var profile = saveServer.GetProfile(profileId);
        var pmc = profile.CharacterData!.PmcData!;
        var nonItemChanges = new List<string>();

        // 同步 hideoutAreaStashes（保险柜区域等）
        if (preview.HideoutStashAdditions.Count > 0)
        {
            pmc.Inventory ??= new BotBaseInventory();
            pmc.Inventory.HideoutAreaStashes ??= new Dictionary<string, MongoId>();
            foreach (var (key, value) in preview.HideoutStashAdditions)
            {
                if (!pmc.Inventory.HideoutAreaStashes.ContainsKey(key))
                {
                    pmc.Inventory.HideoutAreaStashes[key] = value;
                    nonItemChanges.Add($"hideoutAreaStashes[{key}]={value}");
                }
            }
        }

        // 同步狗牌 _tpl
        if (preview.DogTagTemplateChange != null)
        {
            pmc.Customization ??= new Customization();
            pmc.Customization.DogTag = preview.DogTagTemplateChange.NewTemplate;
            nonItemChanges.Add($"DogTag={preview.DogTagTemplateChange.NewTemplate}");
        }

        // 同步 trader 初始声望/等级（仅升不降；玩家已自行刷高的不动）
        foreach (var upgrade in preview.TraderInfoUpgrades)
        {
            pmc.TradersInfo ??= new Dictionary<MongoId, TraderInfo>();
            if (!pmc.TradersInfo.TryGetValue(upgrade.TraderId, out var info))
            {
                info = new TraderInfo();
                pmc.TradersInfo[upgrade.TraderId] = info;
            }

            if (upgrade.NewLoyaltyLevel.HasValue)
            {
                info.LoyaltyLevel = upgrade.NewLoyaltyLevel;
                nonItemChanges.Add($"trader[{upgrade.TraderId}].loyalty={upgrade.NewLoyaltyLevel}");
            }

            if (upgrade.NewStanding.HasValue)
            {
                info.Standing = upgrade.NewStanding;
                nonItemChanges.Add($"trader[{upgrade.TraderId}].standing={upgrade.NewStanding}");
            }
        }

        // 发邮件
        if (preview.ItemTotalCount > 0)
        {
            var allItems = preview.ItemBundles.SelectMany(b => b.Items).ToList();
            var config = UpgradeConfig;
            var body = (string.IsNullOrWhiteSpace(config.MailBodyTemplate) ? DefaultMailBody : config.MailBodyTemplate)
                .Replace("{from}", preview.FromEdition)
                .Replace("{to}", preview.ToEdition)
                .Replace("{count}", preview.ItemTotalCount.ToString());

            mailSendService.SendSystemMessageToPlayer(
                profileId,
                body,
                allItems,
                maxStorageTimeSeconds: Math.Max(1, config.MailExpiryDays) * 24L * 3600L
            );
        }

        // 落库
        try
        {
            saveServer.SaveProfileAsync(profileId).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.Error($"[EditionUpgrade] 持久化失败 profileId={profileId}: {ex.Message}");
            return new UpgradeResult { Success = false, Message = $"持久化失败: {ex.Message}" };
        }

        // 审计日志（复用激活码日志表，事件类型 upgrade）
        activationLog.AppendUpgradeLog(
            profileId.ToString(),
            preview.FromEdition,
            preview.ToEdition,
            preview.ItemTotalCount,
            operatorName,
            profile.ProfileInfo!.Username,
            preview.RawEdition
        );

        logger.Success(
            $"[EditionUpgrade] {profile.ProfileInfo!.Username} ({profileId}) {preview.FromEdition} → {preview.ToEdition}: "
                + $"{preview.ItemTotalCount} 件物品 / {nonItemChanges.Count} 项非物品变更（操作员={operatorName}）"
        );

        return new UpgradeResult
        {
            Success = true,
            Message = $"升级完成：发送 {preview.ItemTotalCount} 件物品，同步 {nonItemChanges.Count} 项非物品变更",
            ItemCount = preview.ItemTotalCount,
            NonItemChanges = nonItemChanges,
        };
    }

    // ---- 内部：差量算法 ----

    /// <summary>
    /// 选择正确的边模板：优先按玩家 Side（Bear/Usec）；缺失时回退另一边。
    /// </summary>
    internal static (TemplateSide? source, TemplateSide? target) SelectSides(ProfileSides sourceTemplate, ProfileSides targetTemplate, string? side)
    {
        var isBear = string.Equals(side, "Bear", StringComparison.OrdinalIgnoreCase);
        return (
            isBear ? (sourceTemplate.Bear ?? sourceTemplate.Usec) : (sourceTemplate.Usec ?? sourceTemplate.Bear),
            isBear ? (targetTemplate.Bear ?? targetTemplate.Usec) : (targetTemplate.Usec ?? targetTemplate.Bear)
        );
    }

    /// <summary>
    /// 计算物品捆绑差：以 stash 容器为根，把目标模板里所有「玩家根容器/装备外」物品按根分组。
    /// 玩家已有相同 _tpl + 关键 upd 子集签名的根物品按数量抵扣；仍缺失的根物品整组发送（含子物品）。
    /// </summary>
    internal static List<UpgradeItemBundle> ComputeItemBundleDiff(TemplateSide source, TemplateSide target, Models.Eft.Common.PmcData playerPmc)
    {
        var sourceItems = source.Character?.Inventory?.Items ?? new List<Item>();
        var targetItems = target.Character?.Inventory?.Items ?? new List<Item>();
        var playerItems = playerPmc.Inventory?.Items ?? new List<Item>();

        // 找到目标模板的 stash 主容器 id
        var targetStashId = target.Character?.Inventory?.Stash;
        var sourceStashId = source.Character?.Inventory?.Stash;

        // 收集"在 stash 内的根物品"：parentId == stashId
        var sourceStashRoots = sourceItems
            .Where(it => sourceStashId.HasValue && it.ParentId == sourceStashId.Value.ToString())
            .Select(it => SignatureOf(it))
            .ToList();
        var targetStashRoots = targetItems
            .Where(it => targetStashId.HasValue && it.ParentId == targetStashId.Value.ToString())
            .ToList();

        // 玩家 stash id（活档可能与模板不同，因此用玩家自身 stash 字段）
        var playerStashId = playerPmc.Inventory?.Stash;
        var playerStashRoots = playerItems
            .Where(it => playerStashId.HasValue && it.ParentId == playerStashId.Value.ToString())
            .Select(it => SignatureOf(it))
            .ToList();

        // 已有签名计数 = 源模板 stash 根 + 玩家当前 stash 根（避免新档玩家直接被判定缺失）
        var ownedSignatures = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var sig in sourceStashRoots.Concat(playerStashRoots))
        {
            ownedSignatures[sig] = ownedSignatures.GetValueOrDefault(sig) + 1;
        }

        // 索引子物品：按 parentId 分组
        var targetChildrenByParent = targetItems
            .Where(it => it.Id != default && !string.IsNullOrEmpty(it.ParentId) && it.ParentId != targetStashId?.ToString())
            .ToLookup(it => it.ParentId!, StringComparer.Ordinal);

        var bundles = new List<UpgradeItemBundle>();
        foreach (var root in targetStashRoots)
        {
            var sig = SignatureOf(root);
            if (ownedSignatures.TryGetValue(sig, out var count) && count > 0)
            {
                ownedSignatures[sig] = count - 1;
                continue;
            }

            // 整组复制：根 + 所有递归子物品；为避免与玩家档冲突，全部分配新 MongoId（保持 parent/child 拓扑）
            var collected = new List<Item>();
            CollectSubtree(root, targetChildrenByParent, collected);

            // 重新分配 id 并保留拓扑
            var idMap = new Dictionary<string, MongoId>(StringComparer.Ordinal);
            var rebased = new List<Item>(collected.Count);
            foreach (var src in collected)
            {
                var newId = new MongoId();
                idMap[src.Id.ToString()] = newId;
                rebased.Add(new Item
                {
                    Id = newId,
                    Template = src.Template,
                    SlotId = src.SlotId,
                    Location = src.Location,
                    Upd = src.Upd,
                });
            }

            for (var i = 0; i < collected.Count; i++)
            {
                var src = collected[i];
                if (!string.IsNullOrEmpty(src.ParentId) && idMap.TryGetValue(src.ParentId, out var newParent))
                {
                    rebased[i] = rebased[i] with { ParentId = newParent.ToString() };
                }
            }

            bundles.Add(new UpgradeItemBundle
            {
                RootTemplate = root.Template.ToString(),
                Items = rebased,
            });
        }

        return bundles;
    }

    /// <summary>
    /// 物品签名：_tpl + 关键 upd 字段子集。命中代表两件物品在玩家视角下"等价"。
    /// </summary>
    internal static string SignatureOf(Item item)
    {
        var u = item.Upd;
        var stack = u?.StackObjectsCount?.ToString("0.###") ?? "";
        var resKey = u?.MedKit?.HpResource?.ToString("0.###")
            ?? u?.FoodDrink?.HpPercent?.ToString("0.###")
            ?? u?.RepairKit?.Resource?.ToString("0.###")
            ?? "";
        var dura = u?.Repairable?.Durability?.ToString("0.###") ?? "";
        var maxDura = u?.Repairable?.MaxDurability?.ToString("0.###") ?? "";
        return $"{item.Template}|s={stack}|r={resKey}|d={dura}|m={maxDura}";
    }

    protected static void CollectSubtree(Item root, ILookup<string, Item> childrenByParent, List<Item> sink)
    {
        sink.Add(root);
        foreach (var child in childrenByParent[root.Id.ToString()])
        {
            CollectSubtree(child, childrenByParent, sink);
        }
    }

    /// <summary>仅返回目标独有的 hideout 区域 id 映射；玩家若已有同 key 不覆盖。</summary>
    internal static Dictionary<string, MongoId> ComputeHideoutStashDiff(TemplateSide source, TemplateSide target, Models.Eft.Common.PmcData playerPmc)
    {
        var src = source.Character?.Inventory?.HideoutAreaStashes ?? new Dictionary<string, MongoId>();
        var tgt = target.Character?.Inventory?.HideoutAreaStashes ?? new Dictionary<string, MongoId>();
        var owned = playerPmc.Inventory?.HideoutAreaStashes ?? new Dictionary<string, MongoId>();
        var diff = new Dictionary<string, MongoId>();
        foreach (var (key, value) in tgt)
        {
            if (src.ContainsKey(key))
            {
                continue;
            }

            if (owned.ContainsKey(key))
            {
                continue;
            }

            diff[key] = value;
        }

        return diff;
    }

    internal static DogTagChange? ComputeDogTagDiff(TemplateSide source, TemplateSide target, Models.Eft.Common.PmcData playerPmc)
    {
        var srcDog = source.Character?.Customization?.DogTag;
        var tgtDog = target.Character?.Customization?.DogTag;
        var ownDog = playerPmc.Customization?.DogTag;
        if (!tgtDog.HasValue || tgtDog == srcDog)
        {
            return null;
        }

        if (ownDog == tgtDog)
        {
            return null;
        }

        return new DogTagChange { OldTemplate = ownDog, NewTemplate = tgtDog.Value };
    }

    internal static List<TraderInfoUpgrade> ComputeTraderUpgrades(TemplateSide source, TemplateSide target, Models.Eft.Common.PmcData playerPmc)
    {
        var result = new List<TraderInfoUpgrade>();
        var srcLoyalty = source.Trader?.InitialLoyaltyLevel ?? new Dictionary<MongoId, int?>();
        var tgtLoyalty = target.Trader?.InitialLoyaltyLevel ?? new Dictionary<MongoId, int?>();
        var srcStanding = source.Trader?.InitialStanding ?? new Dictionary<string, double?>();
        var tgtStanding = target.Trader?.InitialStanding ?? new Dictionary<string, double?>();
        var ownInfo = playerPmc.TradersInfo ?? new Dictionary<MongoId, TraderInfo>();

        var allTraderIds = new HashSet<MongoId>(tgtLoyalty.Keys);
        foreach (var k in tgtStanding.Keys)
        {
            if (MongoId.IsValidMongoId(k))
            {
                allTraderIds.Add(new MongoId(k));
            }
        }

        foreach (var trader in allTraderIds)
        {
            var srcLoyaltyLevel = srcLoyalty.GetValueOrDefault(trader);
            var tgtLoyaltyLevel = tgtLoyalty.GetValueOrDefault(trader);
            var srcStandingValue = srcStanding.GetValueOrDefault(trader.ToString());
            var tgtStandingValue = tgtStanding.GetValueOrDefault(trader.ToString());

            ownInfo.TryGetValue(trader, out var owned);
            int? newLoyalty = null;
            if (tgtLoyaltyLevel.HasValue && tgtLoyaltyLevel.Value > (srcLoyaltyLevel ?? 1))
            {
                if ((owned?.LoyaltyLevel ?? 1) < tgtLoyaltyLevel.Value)
                {
                    newLoyalty = tgtLoyaltyLevel.Value;
                }
            }

            double? newStanding = null;
            if (tgtStandingValue.HasValue && tgtStandingValue.Value > (srcStandingValue ?? 0))
            {
                if ((owned?.Standing ?? 0) < tgtStandingValue.Value)
                {
                    newStanding = tgtStandingValue.Value;
                }
            }

            if (newLoyalty.HasValue || newStanding.HasValue)
            {
                result.Add(new TraderInfoUpgrade
                {
                    TraderId = trader,
                    NewLoyaltyLevel = newLoyalty,
                    NewStanding = newStanding,
                });
            }
        }

        return result;
    }

    // ---- 持久化 ----

    protected EditionAliasFile Aliases => aliases ??= LoadJson<EditionAliasFile>(AliasFilePath) ?? new EditionAliasFile();

    protected EditionUpgradeConfig UpgradeConfig => upgradeConfig ??= LoadJson<EditionUpgradeConfig>(UpgradeConfigFilePath) ?? new EditionUpgradeConfig
    {
        MailSubject = DefaultMailSubject,
        MailBodyTemplate = DefaultMailBody,
        MailExpiryDays = 30,
    };

    protected void SaveAliases() => SaveJson(AliasFilePath, Aliases);

    protected void SaveUpgradeConfig() => SaveJson(UpgradeConfigFilePath, UpgradeConfig);

    protected static T? LoadJson<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    protected static void SaveJson<T>(string path, T value)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }

    private const string DefaultMailSubject = "版本升级补齐礼包";
    private const string DefaultMailBody = "您的存档已从 {from} 升级至 {to}，附件中是版本差异礼包共 {count} 件物品，请尽快领取。";
}

// ---- 数据契约 ----

public class EditionAliasFile
{
    [JsonPropertyName("map")]
    public Dictionary<string, string> Map { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class EditionUpgradeConfig
{
    [JsonPropertyName("mailSubject")]
    public string MailSubject { get; set; } = "版本升级补齐礼包";

    [JsonPropertyName("mailBodyTemplate")]
    public string MailBodyTemplate { get; set; } = "您的存档已从 {from} 升级至 {to}，附件中是版本差异礼包共 {count} 件物品，请尽快领取。";

    /// <summary>邮件附件保留天数；最小 1。</summary>
    [JsonPropertyName("mailExpiryDays")]
    public int MailExpiryDays { get; set; } = 30;
}

public class UpgradePreview
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string ProfileId { get; set; } = string.Empty;
    public string FromEdition { get; set; } = string.Empty;
    public string ToEdition { get; set; } = string.Empty;
    public string? RawEdition { get; set; }
    public string? Side { get; set; }
    public int ItemRootCount { get; set; }
    public int ItemTotalCount { get; set; }
    public List<UpgradeItemBundle> ItemBundles { get; set; } = [];
    public Dictionary<string, MongoId> HideoutStashAdditions { get; set; } = new();
    public DogTagChange? DogTagTemplateChange { get; set; }
    public List<TraderInfoUpgrade> TraderInfoUpgrades { get; set; } = [];

    public static UpgradePreview Failed(string message) => new() { Success = false, Message = message };
}

public class UpgradeItemBundle
{
    public string RootTemplate { get; set; } = string.Empty;
    public List<Item> Items { get; set; } = [];
}

public class DogTagChange
{
    public MongoId? OldTemplate { get; set; }
    public MongoId NewTemplate { get; set; }
}

public class TraderInfoUpgrade
{
    public MongoId TraderId { get; set; }
    public int? NewLoyaltyLevel { get; set; }
    public double? NewStanding { get; set; }
}

public class UpgradeResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public int ItemCount { get; set; }
    public List<string> NonItemChanges { get; set; } = [];
}
