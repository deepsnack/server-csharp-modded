using System.Text.Json;

namespace SPTarkov.Server.Core.BattlePass.Administration;

/// <summary>
///     模块命令处理器：统一正常管理员即时写入与协管审核放行的业务逻辑入口。
///     每个业务模块（shop/tasks/tracks/lottery）实现此接口。
/// </summary>
public interface IBattlePassChangeHandler
{
    /// <summary>处理的业务模块（shop / tasks / tracks / lottery）。</summary>
    string Module { get; }

    /// <summary>支持的命令类型列表（如 shop.upsert / shop.delete）。</summary>
    IReadOnlyList<string> CommandTypes { get; }

    /// <summary>执行此命令所需的协管能力（如 shop.submit）。</summary>
    string RequiredCapability { get; }

    /// <summary>规范化输入（trim、大小写、默认值填充）。返回规范化后的强类型对象。</summary>
    object Normalize(string commandType, JsonElement input);

    /// <summary>校验规范化后的输入。返回 null=通过，否则为错误信息。</summary>
    string? Validate(string commandType, object normalizedInput);

    /// <summary>生成变更描述摘要（一句话，用于审核列表折叠行）。</summary>
    string Describe(string commandType, object normalizedInput, object? currentState);

    /// <summary>获取变更目标的稳定键（module:targetType:targetId）。</summary>
    string GetTargetKey(string commandType, object normalizedInput);

    /// <summary>获取变更目标的显示名。</summary>
    string GetTargetDisplayName(string commandType, object normalizedInput);

    /// <summary>获取目标当前快照（用于 baseRevision 和 beforePayload）。不存在返回 null。</summary>
    object? GetCurrentSnapshot(string targetKey);

    /// <summary>计算快照的 revision（SHA-256 of 规范化 JSON）。null 快照返回空字符串。</summary>
    string GetRevision(object? snapshot);

    /// <summary>
    ///     应用变更并激活（原子写入 + 运行时同步）。
    ///     <para>成功返回写入后的新 revision；失败抛异常（调用方负责回滚或标记 failed）。</para>
    ///     <para><paramref name="expectedBaseRevision"/> 不为 null 时需校验当前 revision 一致，不一致抛
    ///     <see cref="ChangeConflictException"/>。</para>
    /// </summary>
    string ApplyAndActivate(string commandType, object normalizedInput, string? expectedBaseRevision, string? changeId);

    /// <summary>
    ///     将单个目标恢复为审核前快照。实现必须先校验当前 revision 等于
    ///     <paramref name="expectedCurrentRevision"/>，并完成对应运行时热同步。
    /// </summary>
    string RestoreSnapshot(string targetKey, object? beforeSnapshot, string expectedCurrentRevision, string? auditId);
}

/// <summary>基线版本冲突——当前配置已被其他变更修改。</summary>
public class ChangeConflictException(string message) : InvalidOperationException(message);

internal static class BattlePassSnapshotCodec
{
    public static T Deserialize<T>(object? snapshot)
    {
        if (snapshot is null)
        {
            throw new InvalidOperationException("回溯快照为空");
        }

        var element = snapshot is JsonElement jsonElement
            ? jsonElement
            : JsonSerializer.SerializeToElement(snapshot);
        return element.Deserialize<T>() ?? throw new InvalidOperationException($"无法解析回溯快照 {typeof(T).Name}");
    }
}
