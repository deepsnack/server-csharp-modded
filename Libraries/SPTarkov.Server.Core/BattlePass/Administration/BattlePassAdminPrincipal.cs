namespace SPTarkov.Server.Core.BattlePass.Administration;

/// <summary>统一管理主体：正常管理员或协管。</summary>
public class BattlePassAdminPrincipal
{
    /// <summary>admin | collaborator</summary>
    public string ActorType { get; set; } = "admin";

    /// <summary>正常管理员固定为 "admin"；协管为 profileId。</summary>
    public string ActorId { get; set; } = "admin";

    /// <summary>管理员或玩家名快照。</summary>
    public string DisplayName { get; set; } = "管理员";

    /// <summary>服务端解析后的能力集合。</summary>
    public HashSet<string> Capabilities { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本次 BattlePass 管理会话 Id。</summary>
    public string SessionId { get; set; } = "";

    /// <summary>签发时间（Unix 秒）。</summary>
    public long IssuedUtc { get; set; }

    /// <summary>绝对过期时间（Unix 秒）。</summary>
    public long ExpiresUtc { get; set; }

    public bool IsAdmin => string.Equals(ActorType, "admin", StringComparison.OrdinalIgnoreCase);
    public bool IsCollaborator => string.Equals(ActorType, "collaborator", StringComparison.OrdinalIgnoreCase);

    /// <summary>检查主体是否拥有指定能力（管理员始终拥有全部能力）。</summary>
    public bool HasCapability(string capability)
    {
        return IsAdmin || Capabilities.Contains("*") || Capabilities.Contains(capability);
    }
}
