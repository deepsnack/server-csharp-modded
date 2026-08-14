using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace SPTarkov.Server.Core.BattlePass.Administration;

/// <summary>任务模块命令处理器：task.upsert / task.delete / task.genSpec。</summary>
[Injectable(InjectionType.Singleton)]
public class TaskChangeHandler(ISptLogger<TaskChangeHandler> logger) : IBattlePassChangeHandler
{
    public string Module => "tasks";
    public IReadOnlyList<string> CommandTypes { get; } = ["task.upsert", "task.delete", "task.genSpec"];
    public string RequiredCapability => "tasks.submit";

    public object Normalize(string commandType, JsonElement input)
    {
        return commandType switch
        {
            "task.upsert" => NormalizeUpsert(input),
            "task.delete" => NormalizeDelete(input),
            "task.genSpec" => NormalizeGenSpec(input),
            _ => throw new ArgumentException($"Unknown commandType: {commandType}"),
        };
    }

    public string? Validate(string commandType, object normalizedInput)
    {
        return commandType switch
        {
            "task.upsert" => ValidateUpsert((BpTaskTemplate) normalizedInput),
            "task.delete" => ValidateDelete((string) normalizedInput),
            "task.genSpec" => null,
            _ => $"不支持的命令类型: {commandType}",
        };
    }

    public string Describe(string commandType, object normalizedInput, object? currentState)
    {
        return commandType switch
        {
            "task.upsert" =>
                currentState is null
                    ? $"新增任务模板: {((BpTaskTemplate) normalizedInput).Id}"
                    : $"编辑任务模板: {((BpTaskTemplate) normalizedInput).Id}",
            "task.delete" => $"删除任务模板: {(string) normalizedInput}",
            "task.genSpec" => "修改任务生成规格",
            _ => commandType,
        };
    }

    public string GetTargetKey(string commandType, object normalizedInput)
    {
        return commandType switch
        {
            "task.upsert" => $"tasks:task:{((BpTaskTemplate) normalizedInput).Id}",
            "task.delete" => $"tasks:task:{(string) normalizedInput}",
            "task.genSpec" => "tasks:genSpec:_global",
            _ => $"tasks:unknown:{commandType}",
        };
    }

    public string GetTargetDisplayName(string commandType, object normalizedInput)
    {
        return commandType switch
        {
            "task.upsert" => ((BpTaskTemplate) normalizedInput).Id ?? "(未命名)",
            "task.delete" => (string) normalizedInput,
            "task.genSpec" => "任务生成规格",
            _ => commandType,
        };
    }

    public object? GetCurrentSnapshot(string targetKey)
    {
        var parts = targetKey.Split(':', 3);
        if (parts.Length < 3) return null;

        return parts[1] switch
        {
            "task" => BattlePassStore.GetTasks()
                .FirstOrDefault(t => string.Equals(t.Id, parts[2], StringComparison.OrdinalIgnoreCase)),
            "genSpec" => BattlePassStore.GetGenSpec(),
            _ => null,
        };
    }

    public string GetRevision(object? snapshot)
    {
        if (snapshot is null) return "";
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexStringLower(hash);
    }

    public string ApplyAndActivate(string commandType, object normalizedInput, string? expectedBaseRevision, string? changeId)
    {
        switch (commandType)
        {
            case "task.upsert":
                return ApplyUpsert((BpTaskTemplate) normalizedInput, expectedBaseRevision);
            case "task.delete":
                return ApplyDelete((string) normalizedInput, expectedBaseRevision);
            case "task.genSpec":
                return ApplyGenSpec((BpGenSpec) normalizedInput, expectedBaseRevision);
            default:
                throw new InvalidOperationException($"不支持的命令类型: {commandType}");
        }
    }

    public string RestoreSnapshot(string targetKey, object? beforeSnapshot, string expectedCurrentRevision, string? auditId)
    {
        var currentRevision = GetRevision(GetCurrentSnapshot(targetKey));
        if (!string.Equals(currentRevision, expectedCurrentRevision, StringComparison.Ordinal))
            throw new ChangeConflictException($"回溯目标 {targetKey} 已发生后续改动: expected={expectedCurrentRevision}, actual={currentRevision}");

        var parts = targetKey.Split(':', 3);
        if (parts.Length < 3) throw new InvalidOperationException($"无效目标键: {targetKey}");
        return parts[1] switch
        {
            "task" when beforeSnapshot is null => ApplyDelete(parts[2], expectedBaseRevision: null),
            "task" => ApplyUpsert(BattlePassSnapshotCodec.Deserialize<BpTaskTemplate>(beforeSnapshot), expectedBaseRevision: null),
            "genSpec" => ApplyGenSpec(BattlePassSnapshotCodec.Deserialize<BpGenSpec>(beforeSnapshot), expectedBaseRevision: null),
            _ => throw new InvalidOperationException($"不支持回溯目标: {targetKey}"),
        };
    }

    // ---- Normalize ----

    private static BpTaskTemplate NormalizeUpsert(JsonElement input)
    {
        var task = JsonSerializer.Deserialize<BpTaskTemplate>(input.GetRawText())
            ?? throw new ArgumentException("无法反序列化任务模板");
        task.Id = task.Id?.Trim() ?? "";
        return task;
    }

    private static string NormalizeDelete(JsonElement input)
    {
        var id = input.TryGetProperty("id", out var idProp) ? idProp.GetString()?.Trim() : null;
        return id ?? throw new ArgumentException("缺少 id");
    }

    private static BpGenSpec NormalizeGenSpec(JsonElement input)
    {
        var spec = JsonSerializer.Deserialize<BpGenSpec>(input.GetRawText())
            ?? throw new ArgumentException("无法反序列化生成规格");
        NormalizeScope(spec.Daily ??= new BpGenScopeSpec());
        NormalizeScope(spec.Weekly ??= new BpGenScopeSpec());
        NormalizeScope(spec.Season ??= new BpGenScopeSpec());
        return spec;
    }

    private static void NormalizeScope(BpGenScopeSpec scope)
    {
        scope.Count = Math.Clamp(scope.Count, 1, 100);
        scope.AutoPeriodHours = Math.Max(0, scope.AutoPeriodHours);
        scope.ConditionTypes = (scope.ConditionTypes ?? [])
            .Where(t => t is not null && (t.Equals("Kills", StringComparison.OrdinalIgnoreCase)
                || t.Equals("Exploration", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        scope.KillTargets = (scope.KillTargets ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        scope.Locations = (scope.Locations ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        scope.MinCount = Math.Max(1, scope.MinCount);
        scope.MaxCount = Math.Max(scope.MinCount, scope.MaxCount);
        scope.XpEasy = Math.Max(0, scope.XpEasy);
        scope.XpMed = Math.Max(0, scope.XpMed);
        scope.XpHard = Math.Max(0, scope.XpHard);
    }

    // ---- Validate ----

    private static string? ValidateUpsert(BpTaskTemplate task)
    {
        return BattlePassTaskRules.Validate(task);
    }

    private static string? ValidateDelete(string id)
    {
        return string.IsNullOrWhiteSpace(id) ? "缺少 id" : null;
    }

    // ---- Apply ----

    private string ApplyUpsert(BpTaskTemplate task, string? expectedBaseRevision)
    {
        if (expectedBaseRevision is not null)
        {
            var current = GetCurrentSnapshot($"tasks:task:{task.Id}");
            var currentRev = GetRevision(current);
            if (currentRev != expectedBaseRevision)
                throw new ChangeConflictException($"任务 {task.Id} 基线版本冲突: expected={expectedBaseRevision}, actual={currentRev}");
        }

        var tasks = BattlePassStore.GetTasks();
        tasks.RemoveAll(t => string.Equals(t.Id, task.Id, StringComparison.OrdinalIgnoreCase));
        tasks.Add(task);
        BattlePassStore.SaveTasks(tasks);

        var newSnapshot = GetCurrentSnapshot($"tasks:task:{task.Id}");
        return GetRevision(newSnapshot);
    }

    private string ApplyDelete(string id, string? expectedBaseRevision)
    {
        if (expectedBaseRevision is not null)
        {
            var current = GetCurrentSnapshot($"tasks:task:{id}");
            var currentRev = GetRevision(current);
            if (currentRev != expectedBaseRevision)
                throw new ChangeConflictException($"任务 {id} 基线版本冲突: expected={expectedBaseRevision}, actual={currentRev}");
        }

        var tasks = BattlePassStore.GetTasks();
        tasks.RemoveAll(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        BattlePassStore.SaveTasks(tasks);

        return GetRevision(null);
    }

    private string ApplyGenSpec(BpGenSpec spec, string? expectedBaseRevision)
    {
        if (expectedBaseRevision is not null)
        {
            var current = GetCurrentSnapshot("tasks:genSpec:_global");
            var currentRev = GetRevision(current);
            if (currentRev != expectedBaseRevision)
                throw new ChangeConflictException($"任务生成规格基线版本冲突: expected={expectedBaseRevision}, actual={currentRev}");
        }

        BattlePassStore.SaveGenSpec(spec);

        var newSnapshot = GetCurrentSnapshot("tasks:genSpec:_global");
        return GetRevision(newSnapshot);
    }
}
