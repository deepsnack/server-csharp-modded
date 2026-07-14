namespace SPTarkov.Server.Core.BattlePass;

/// <summary>任务模板的共享服务端校验规则，供管理员直写与协管审核两条入口共同使用。</summary>
public static class BattlePassTaskRules
{
    private static readonly HashSet<string> OneLifeConditionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Kills",
        "FindItem",
        "PlaceItem",
        "VisitZone",
    };

    public static bool SupportsOneLife(string? conditionType)
    {
        return !string.IsNullOrWhiteSpace(conditionType) && OneLifeConditionTypes.Contains(conditionType.Trim());
    }

    public static string? Validate(BpTaskTemplate task)
    {
        if (string.IsNullOrWhiteSpace(task.Id))
        {
            return "任务 id 不能为空";
        }

        if (task.OneLife && !SupportsOneLife(task.ConditionType))
        {
            return "一命完成仅支持击杀、寻找物品、安放物品和到达地点任务";
        }

        // 敌我装备在服务端战绩和客户端击杀事件中都无从完整判定，保留拦截；
        // 枪身/口径/武器改件条件由客户端击杀瞬间上报支持（走 supplemental 结算）。
        if (string.Equals(task.ConditionType, "Kills", StringComparison.OrdinalIgnoreCase)
            && ((task.EnemyEquipment?.Count ?? 0) > 0
                || (task.PlayerEquipment?.Count ?? 0) > 0))
        {
            return "战后记录不含敌我装备，无法作为击杀任务条件";
        }

        return null;
    }
}
