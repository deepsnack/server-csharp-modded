using System.Text.Json.Serialization;

namespace SPTarkov.Server.Core.BattlePass;

public record QuestSkipStateResult
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("ticketCount")]
    public int TicketCount { get; init; }

    [JsonPropertyName("inRaid")]
    public bool InRaid { get; init; }

    [JsonPropertyName("tasks")]
    public List<QuestSkipTaskView> Tasks { get; init; } = [];
}

public record QuestSkipTaskView
{
    [JsonPropertyName("titleZh")]
    public required string TitleZh { get; init; }

    [JsonPropertyName("objectives")]
    public List<QuestSkipObjectiveView> Objectives { get; init; } = [];
}

public record QuestSkipObjectiveView
{
    /// <summary>短期随机操作句柄；不包含任务或条件 MongoID。</summary>
    [JsonPropertyName("actionId")]
    public string? ActionId { get; init; }

    [JsonPropertyName("conditionNameZh")]
    public required string ConditionNameZh { get; init; }

    [JsonPropertyName("conditionDescriptionZh")]
    public required string ConditionDescriptionZh { get; init; }

    [JsonPropertyName("completed")]
    public bool Completed { get; init; }

    [JsonPropertyName("canSkip")]
    public bool CanSkip { get; init; }
}

public record QuestSkipResult
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("ticketCount")]
    public int TicketCount { get; init; }

    [JsonPropertyName("questAvailableForFinish")]
    public bool QuestAvailableForFinish { get; init; }
}

public record QuestSkipRequest
{
    [JsonPropertyName("actionId")]
    public string? ActionId { get; init; }
}
