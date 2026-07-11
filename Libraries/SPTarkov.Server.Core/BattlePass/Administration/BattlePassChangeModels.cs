using System.Text.Json.Serialization;

namespace SPTarkov.Server.Core.BattlePass.Administration;

/// <summary>待审核变更请求。</summary>
public class BpChangeRequest
{
    public int SchemaVersion { get; set; } = 2;
    public string Id { get; set; } = "";

    /// <summary>待审内容版本；协管每次编辑都会递增，用于阻止管理员批准过期内容。</summary>
    public int Version { get; set; } = 1;

    /// <summary>业务模块：shop | tasks | tracks | lottery。</summary>
    public string Module { get; set; } = "";

    /// <summary>由服务端白名单映射的命令类型。</summary>
    public string CommandType { get; set; } = "";

    /// <summary>操作类型：create | update | delete。</summary>
    public string Operation { get; set; } = "";

    public string TargetType { get; set; } = "";
    public string TargetId { get; set; } = "";
    public string TargetDisplayName { get; set; } = "";

    /// <summary>module + targetType + targetId 的稳定键。</summary>
    public string TargetKey { get; set; } = "";

    /// <summary>一句话变更摘要。</summary>
    public string Summary { get; set; } = "";

    public BpChangeActor Actor { get; set; } = new();
    public long CreatedUtc { get; set; }
    public long UpdatedUtc { get; set; }

    /// <summary>提交时目标规范化 JSON 的 SHA-256。</summary>
    public string BaseRevision { get; set; } = "";

    /// <summary>提交时快照；创建操作为 null。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? BeforePayload { get; set; }

    /// <summary>协管原始提交。所有操作（包括删除）都保留可重新交给 handler.Normalize 的输入形状。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? ProposedPayload { get; set; }

    /// <summary>管理员编辑后的最终内容；未编辑时为 null。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? FinalPayload { get; set; }

    /// <summary>pending | applying | applied | rejected | conflict | failed | withdrawn</summary>
    public string Status { get; set; } = "pending";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BpChangeActor? Reviewer { get; set; }

    public long ReviewedUtc { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReviewReason { get; set; }

    public long AppliedUtc { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResultRevision { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FailureCode { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FailureMessage { get; set; }

    public List<BpChangeEvent> History { get; set; } = new();
}

/// <summary>变更操作者。</summary>
public class BpChangeActor
{
    public string ActorId { get; set; } = "";
    public string DisplayName { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProfileId { get; set; }
}

/// <summary>变更历史事件。</summary>
public class BpChangeEvent
{
    public string Type { get; set; } = ""; // submit / edit / approve / reject / apply / recover / notify
    public long Timestamp { get; set; }
    public string ActorId { get; set; } = "";
    public string ActorDisplayName { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; set; }
}

/// <summary>协管授权记录。</summary>
public class BpCollaboratorGrant
{
    public int SchemaVersion { get; set; } = 1;
    public string ProfileId { get; set; } = "";
    public string UsernameSnapshot { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public List<string> Capabilities { get; set; } = new();

    public string GrantedBy { get; set; } = "";
    public long GrantedUtc { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UpdatedBy { get; set; }

    public long UpdatedUtc { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RevokedBy { get; set; }

    public long RevokedUtc { get; set; }
    public int Version { get; set; } = 1;
}

/// <summary>协管与管理员审核操作的独立审计记录。</summary>
public class BpAuditLogEntry
{
    public string Id { get; set; } = "";
    public string EventType { get; set; } = ""; // submit / approve / reject / rollback
    public string ChangeId { get; set; } = "";
    public string Module { get; set; } = "";
    public string CommandType { get; set; } = "";
    public string Operation { get; set; } = "";
    public string TargetKey { get; set; } = "";
    public string TargetDisplayName { get; set; } = "";
    public string Summary { get; set; } = "";
    public BpChangeActor Actor { get; set; } = new();
    public BpChangeActor SubmittedBy { get; set; } = new();
    public long CreatedUtc { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? BeforePayload { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? AfterPayload { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResultRevision { get; set; }

    public bool Reversible { get; set; }
    public bool RolledBack { get; set; }
    public long RolledBackUtc { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BpChangeActor? RolledBackBy { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RollbackResultRevision { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RollbackOfAuditId { get; set; }
}
