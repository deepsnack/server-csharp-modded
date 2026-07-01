using System.Text.RegularExpressions;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.BattlePass.ItemControl;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Enums.Hideout;
using SPTarkov.Server.Core.Services;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>把原生及动态任务结构转换为只含中文、无内部标识的玩家展示文本。</summary>
[Injectable]
public partial class QuestSkipChineseService(LocaleService localeService, ItemSearchService itemSearchService)
{
    private static readonly Dictionary<string, string> ConditionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FindItem"] = "寻找物品",
        ["HandoverItem"] = "上交物品",
        ["LeaveItemAtLocation"] = "放置物品",
        ["PlaceBeacon"] = "安装信标",
        ["WeaponAssembly"] = "武器改装",
        ["SellItemToTrader"] = "出售物品",
        ["VisitPlace"] = "探索地点",
        ["Skill"] = "技能等级",
        ["TraderLoyalty"] = "商人忠诚度",
        ["TraderStanding"] = "商人好感度",
        ["Quest"] = "前置任务",
        ["HideoutArea"] = "藏身处建设",
        ["GlobalVariableValue"] = "全局任务进度",
        ["Level"] = "角色等级",
        ["Experience"] = "角色经验",
        ["Location"] = "地图限制",
        ["ExitStatus"] = "安全撤离",
        ["ExitName"] = "指定出口撤离",
        ["Kills"] = "击杀目标",
        ["Shots"] = "射击目标",
        ["InZone"] = "指定区域行动",
        ["Equipment"] = "装备限制",
        ["HealthEffect"] = "状态限制",
        ["HealthBuff"] = "增益状态限制",
        ["Time"] = "时间限制",
        ["LaunchFlare"] = "发射信号弹",
        ["UnderArtilleryFire"] = "炮火区域行动",
        ["ArenaGameMode"] = "竞技模式限制",
        ["ArenaMatchPlace"] = "竞技排名",
        ["ArenaPlayerInTeamPlace"] = "队内排名",
    };

    private static readonly Dictionary<QuestTypeEnum, string> QuestTypeNames = new()
    {
        [QuestTypeEnum.PickUp] = "物资搜寻",
        [QuestTypeEnum.Elimination] = "歼灭行动",
        [QuestTypeEnum.Discover] = "地点探索",
        [QuestTypeEnum.Completion] = "物资征集",
        [QuestTypeEnum.Exploration] = "安全撤离",
        [QuestTypeEnum.Levelling] = "等级提升",
        [QuestTypeEnum.Experience] = "经验积累",
        [QuestTypeEnum.Standing] = "商人关系",
        [QuestTypeEnum.Loyalty] = "忠诚度提升",
        [QuestTypeEnum.Merchant] = "商人交易",
        [QuestTypeEnum.Skill] = "技能训练",
        [QuestTypeEnum.Multi] = "综合行动",
        [QuestTypeEnum.WeaponAssembly] = "武器改装",
        [QuestTypeEnum.ArenaWinMatch] = "竞技获胜",
        [QuestTypeEnum.ArenaWinRound] = "回合获胜",
    };

    private static readonly Dictionary<string, string> TargetNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Any"] = "任意目标",
        ["Savage"] = "拾荒者目标",
        ["Scav"] = "拾荒者目标",
        ["Pmc"] = "私人军事承包商",
        ["Usec"] = "西方阵营雇佣兵",
        ["Bear"] = "俄方阵营雇佣兵",
        ["boss"] = "首领目标",
        ["factory4_day"] = "工厂",
        ["factory4_night"] = "工厂",
        ["bigmap"] = "海关",
        ["woods"] = "森林",
        ["shoreline"] = "海岸线",
        ["interchange"] = "立交桥",
        ["rezervbase"] = "储备站",
        ["laboratory"] = "实验室",
        ["lighthouse"] = "灯塔",
        ["tarkovstreets"] = "塔科夫街区",
        ["sandbox"] = "中心区",
        ["sandbox_high"] = "中心区",
        ["labyrinth"] = "迷宫",
    };

    private static readonly Dictionary<HideoutAreas, string> HideoutNames = new()
    {
        [HideoutAreas.Vents] = "通风系统",
        [HideoutAreas.Security] = "安保系统",
        [HideoutAreas.WaterCloset] = "卫生间",
        [HideoutAreas.Stash] = "仓库",
        [HideoutAreas.Generator] = "发电机",
        [HideoutAreas.Heating] = "供暖系统",
        [HideoutAreas.WaterCollector] = "集水器",
        [HideoutAreas.MedStation] = "医疗站",
        [HideoutAreas.Kitchen] = "营养部",
        [HideoutAreas.RestSpace] = "休息区",
        [HideoutAreas.Workbench] = "工作台",
        [HideoutAreas.IntelligenceCenter] = "情报中心",
        [HideoutAreas.ShootingRange] = "射击场",
        [HideoutAreas.Library] = "图书馆",
        [HideoutAreas.ScavCase] = "拾荒者宝箱",
        [HideoutAreas.Illumination] = "照明系统",
        [HideoutAreas.PlaceOfFame] = "名人堂",
        [HideoutAreas.AirFilteringUnit] = "空气过滤装置",
        [HideoutAreas.SolarPower] = "太阳能系统",
        [HideoutAreas.BoozeGenerator] = "酿酒器",
        [HideoutAreas.BitcoinFarm] = "比特币矿场",
        [HideoutAreas.Gym] = "健身房",
        [HideoutAreas.WeaponStand] = "武器架",
        [HideoutAreas.WeaponStandSecondary] = "副武器架",
        [HideoutAreas.EquipmentPresetsStand] = "装备预设架",
        [HideoutAreas.CircleOfCultists] = "邪教徒之环",
    };

    public string QuestTitle(Quest quest, bool isRepeatable, string? repeatableGroupName)
    {
        if (!isRepeatable)
        {
            var ch = localeService.GetLocaleDb("ch");
            foreach (var key in new[] { $"{quest.Id} name", quest.Name })
            {
                if (!string.IsNullOrWhiteSpace(key)
                    && ch.TryGetValue(key, out var value)
                    && IsUsableChinese(value, key))
                {
                    return Sanitize(value);
                }
            }

            return $"固定任务：{QuestTypeName(quest.Type)}";
        }

        var cycle = repeatableGroupName?.Contains("weekly", StringComparison.OrdinalIgnoreCase) == true
            || repeatableGroupName?.Contains("每周", StringComparison.OrdinalIgnoreCase) == true
            ? "每周任务"
            : "每日任务";
        return $"{cycle}：{QuestTypeName(quest.Type)}";
    }

    public string ConditionName(QuestCondition condition)
    {
        if (!string.Equals(condition.ConditionType, "CounterCreator", StringComparison.OrdinalIgnoreCase))
        {
            return ConditionNames.GetValueOrDefault(condition.ConditionType, "特殊任务条件");
        }

        var nested = condition.Counter?.Conditions ?? [];
        foreach (var preferred in new[] { "Kills", "ExitName", "ExitStatus", "VisitPlace", "InZone", "Shots", "Location" })
        {
            if (nested.Any(item => string.Equals(item.ConditionType, preferred, StringComparison.OrdinalIgnoreCase)))
            {
                if (preferred is "ExitName" or "ExitStatus"
                    && nested.Any(item => string.Equals(item.ConditionType, "Location", StringComparison.OrdinalIgnoreCase)))
                {
                    return "指定地图撤离";
                }

                return ConditionNames.GetValueOrDefault(preferred, "特殊任务条件");
            }
        }

        var firstMapped = nested.Select(item => ConditionNames.GetValueOrDefault(item.ConditionType)).FirstOrDefault(name => name is not null);
        return firstMapped ?? "特殊任务条件";
    }

    public string ConditionDescription(QuestCondition condition, bool isRepeatable)
    {
        if (!isRepeatable)
        {
            var key = condition.Id.ToString();
            var ch = localeService.GetLocaleDb("ch");
            if (ch.TryGetValue(key, out var localized) && IsUsableChinese(localized, key))
            {
                return Sanitize(localized);
            }
        }

        return Sanitize(BuildDescription(condition));
    }

    internal string ConditionNameForType(string conditionType)
    {
        return ConditionNames.GetValueOrDefault(conditionType, "特殊任务条件");
    }

    private string BuildDescription(QuestCondition condition)
    {
        var count = FormatCount(condition.Value);
        var target = ResolveTargets(condition.Target);
        var fir = condition.OnlyFoundInRaid == true ? "（需在战局中找到）" : "";
        switch (condition.ConditionType)
        {
            case "FindItem":
                return $"找到{count}件{target ?? "指定物品"}{fir}";
            case "HandoverItem":
                return $"上交{count}件{target ?? "指定物品"}{fir}";
            case "SellItemToTrader":
                return $"向指定商人出售{count}件{target ?? "指定物品"}";
            case "LeaveItemAtLocation":
                return $"在指定地点放置{count}件{target ?? "指定物品"}";
            case "PlaceBeacon":
                return "在指定地点安装信标";
            case "WeaponAssembly":
                return $"按要求改装{target ?? "指定武器"}";
            case "VisitPlace":
                return "到达指定地点";
            case "Skill":
                return $"将指定技能提升至{count}级";
            case "TraderLoyalty":
                return $"将指定商人忠诚度提升至{count}级";
            case "TraderStanding":
                return $"将指定商人好感度提升至{count}";
            case "Quest":
                return "完成指定前置任务";
            case "HideoutArea":
                var area = condition.AreaType is not null && HideoutNames.TryGetValue(condition.AreaType.Value, out var areaName)
                    ? areaName
                    : "指定藏身处设施";
                return $"将{area}建设至{count}级";
            case "CounterCreator":
                return BuildCounterDescription(condition);
            default:
                return $"完成{ConditionName(condition)}要求";
        }
    }

    private string BuildCounterDescription(QuestCondition condition)
    {
        var nested = condition.Counter?.Conditions ?? [];
        var count = FormatCount(condition.Value);
        var kill = nested.FirstOrDefault(item => string.Equals(item.ConditionType, "Kills", StringComparison.OrdinalIgnoreCase));
        var exit = nested.FirstOrDefault(item => item.ConditionType is "ExitName" or "ExitStatus");
        var location = nested.FirstOrDefault(item => string.Equals(item.ConditionType, "Location", StringComparison.OrdinalIgnoreCase));
        var place = nested.FirstOrDefault(item => string.Equals(item.ConditionType, "VisitPlace", StringComparison.OrdinalIgnoreCase));
        var zones = nested.FirstOrDefault(item => string.Equals(item.ConditionType, "InZone", StringComparison.OrdinalIgnoreCase));

        var mapText = ResolveTargets(location?.Target);
        if (kill is not null)
        {
            var enemy = ResolveTargets(kill.Target) ?? "指定目标";
            return string.IsNullOrWhiteSpace(mapText)
                ? $"击杀{count}名{enemy}"
                : $"在{mapText}击杀{count}名{enemy}";
        }

        if (exit is not null)
        {
            return string.IsNullOrWhiteSpace(mapText)
                ? $"安全撤离{count}次"
                : $"从{mapText}安全撤离{count}次";
        }

        if (place is not null)
        {
            return "到达指定地点";
        }

        if (zones is not null)
        {
            return "在指定区域完成行动";
        }

        var mapped = nested.Select(item => ConditionNames.GetValueOrDefault(item.ConditionType)).FirstOrDefault(name => name is not null);
        return mapped is null ? "完成特殊任务条件" : $"完成{mapped}要求";
    }

    private string? ResolveTargets(SPTarkov.Server.Core.Utils.Json.ListOrT<string>? value)
    {
        if (value is null)
        {
            return null;
        }

        var raw = value.IsItem ? [value.Item!] : value.List ?? [];
        var resolved = raw.Select(ResolveTarget).Where(text => text is not null).Distinct().Take(3).ToList();
        return resolved.Count == 0 ? null : string.Join("、", resolved!);
    }

    private string? ResolveTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        if (TargetNames.TryGetValue(target, out var known))
        {
            return known;
        }

        var ch = localeService.GetLocaleDb("ch");
        foreach (var key in new[] { target, $"{target} Name", $"{target} name" })
        {
            if (ch.TryGetValue(key, out var localized) && IsUsableChinese(localized, key))
            {
                return Sanitize(localized);
            }
        }

        if (MongoId.IsValidMongoId(target))
        {
            var itemName = itemSearchService.ResolveItemNameZh(new MongoId(target));
            if (ContainsChinese(itemName))
            {
                return Sanitize(itemName);
            }
        }

        return null;
    }

    private static string QuestTypeName(QuestTypeEnum type) => QuestTypeNames.GetValueOrDefault(type, "综合行动");

    private static string FormatCount(double? value)
    {
        var number = Math.Max(1, value ?? 1);
        return Math.Abs(number - Math.Round(number)) < 0.0001 ? ((int)Math.Round(number)).ToString() : number.ToString("0.##");
    }

    private static bool IsUsableChinese(string? value, string key)
    {
        return !string.IsNullOrWhiteSpace(value)
            && !string.Equals(value.Trim(), key.Trim(), StringComparison.OrdinalIgnoreCase)
            && ContainsChinese(value)
            && !MongoIdRegex().IsMatch(value.Trim());
    }

    private static bool ContainsChinese(string? value) => !string.IsNullOrEmpty(value) && ChineseRegex().IsMatch(value);

    private static string Sanitize(string value)
    {
        var text = HtmlTagRegex().Replace(value, "");
        text = Regex.Replace(text, "(?i)\\bPMC\\b", "私人军事承包商");
        text = Regex.Replace(text, "(?i)\\bScav\\b", "拾荒者");
        text = Regex.Replace(text, "(?i)\\bUSEC\\b", "西方阵营");
        text = Regex.Replace(text, "(?i)\\bBEAR\\b", "俄方阵营");
        text = MongoIdRegex().Replace(text, "指定目标");
        text = InternalTypeRegex().Replace(text, "特殊任务条件");
        return WhitespaceRegex().Replace(text, " ").Trim();
    }

    [GeneratedRegex("[\\u3400-\\u9fff]")]
    private static partial Regex ChineseRegex();

    [GeneratedRegex("(?i)(?<![0-9a-f])[0-9a-f]{24}(?![0-9a-f])")]
    private static partial Regex MongoIdRegex();

    [GeneratedRegex("(?i)\\b(?:CounterCreator|HandoverItem|FindItem|LeaveItemAtLocation|PlaceBeacon|WeaponAssembly|SellItemToTrader|UNKNOWN)\\b")]
    private static partial Regex InternalTypeRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();
}
