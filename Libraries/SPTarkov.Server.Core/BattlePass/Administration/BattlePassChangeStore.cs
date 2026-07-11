using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace SPTarkov.Server.Core.BattlePass.Administration;

/// <summary>
///     待审核变更、协管授权和审计日志的持久化存储。
///     <para>写入使用 tmp+flush+rename 原子替换，保留 .bak 备份。</para>
/// </summary>
[Injectable(InjectionType.Singleton)]
public class BattlePassChangeStore(ISptLogger<BattlePassChangeStore> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static string BaseDir => Path.Combine(Directory.GetCurrentDirectory(), "SPT_Data", "battlepass", "admin");
    private static string ChangesDir => Path.Combine(BaseDir, "changes");
    private static string AuditDir => Path.Combine(BaseDir, "audit");
    private static string QuarantineDir => Path.Combine(BaseDir, "quarantine");
    private static string CollaboratorsPath => Path.Combine(BaseDir, "collaborators.json");

    private readonly object _changeLock = new();
    private readonly object _grantLock = new();
    private readonly object _auditLock = new();

    // ---- 协管授权 ----

    public List<BpCollaboratorGrant> GetGrants()
    {
        lock (_grantLock)
        {
            return AtomicRead<List<BpCollaboratorGrant>>(CollaboratorsPath) ?? new();
        }
    }

    public void SaveGrants(List<BpCollaboratorGrant> grants)
    {
        lock (_grantLock)
        {
            AtomicWrite(CollaboratorsPath, grants);
        }
    }

    // ---- 变更请求 ----

    public BpChangeRequest? GetChange(string changeId)
    {
        lock (_changeLock)
        {
            var path = ChangeFilePath(changeId);
            return ReadAndMigrateChange(path);
        }
    }

    public void SaveChange(BpChangeRequest change)
    {
        lock (_changeLock)
        {
            var path = ChangeFilePath(change.Id);
            AtomicWrite(path, change);
        }
    }

    public List<BpChangeRequest> QueryChanges(string? module = null, string? status = null, string? actorId = null, int limit = 50)
    {
        lock (_changeLock)
        {
            Directory.CreateDirectory(ChangesDir);
            var files = Directory.GetFiles(ChangesDir, "*.json");
            var results = new List<BpChangeRequest>();

            foreach (var file in files)
            {
                var item = ReadAndMigrateChange(file);
                if (item is null) continue;
                if (module is not null && !string.Equals(item.Module, module, StringComparison.OrdinalIgnoreCase)) continue;
                if (status is not null && !string.Equals(item.Status, status, StringComparison.OrdinalIgnoreCase)) continue;
                if (actorId is not null && !string.Equals(item.Actor.ActorId, actorId, StringComparison.OrdinalIgnoreCase)) continue;
                results.Add(item);
            }

            return results.OrderByDescending(r => r.CreatedUtc).Take(limit).ToList();
        }
    }

    /// <summary>查找同一 targetKey 下是否存在 pending/applying 的请求。</summary>
    public BpChangeRequest? FindPendingByTargetKey(string targetKey)
    {
        lock (_changeLock)
        {
            Directory.CreateDirectory(ChangesDir);
            foreach (var file in Directory.GetFiles(ChangesDir, "*.json"))
            {
                var item = ReadAndMigrateChange(file);
                if (item is null) continue;
                if (string.Equals(item.TargetKey, targetKey, StringComparison.OrdinalIgnoreCase)
                    && item.Status is "pending" or "applying")
                {
                    return item;
                }
            }

            return null;
        }
    }

    /// <summary>查找所有 applying 状态的请求（崩溃恢复用）。</summary>
    public List<BpChangeRequest> FindApplying()
    {
        lock (_changeLock)
        {
            Directory.CreateDirectory(ChangesDir);
            var results = new List<BpChangeRequest>();
            foreach (var file in Directory.GetFiles(ChangesDir, "*.json"))
            {
                var item = ReadAndMigrateChange(file);
                if (item?.Status == "applying")
                {
                    results.Add(item);
                }
            }

            return results;
        }
    }

    // ---- 审计日志 ----

    public void SaveAudit(BpAuditLogEntry entry)
    {
        lock (_auditLock)
        {
            AtomicWrite(Path.Combine(AuditDir, $"{entry.Id}.json"), entry);
        }
    }

    public BpAuditLogEntry? GetAudit(string auditId)
    {
        lock (_auditLock)
        {
            return AtomicRead<BpAuditLogEntry>(Path.Combine(AuditDir, $"{auditId}.json"));
        }
    }

    public List<BpAuditLogEntry> QueryAudit(string? module = null, string? eventType = null, int limit = 100)
    {
        lock (_auditLock)
        {
            Directory.CreateDirectory(AuditDir);
            var results = new List<BpAuditLogEntry>();
            foreach (var file in Directory.GetFiles(AuditDir, "*.json"))
            {
                var entry = AtomicRead<BpAuditLogEntry>(file);
                if (entry is null) continue;
                if (module is not null && !string.Equals(entry.Module, module, StringComparison.OrdinalIgnoreCase)) continue;
                if (eventType is not null && !string.Equals(entry.EventType, eventType, StringComparison.OrdinalIgnoreCase)) continue;
                results.Add(entry);
            }

            return results.OrderByDescending(entry => entry.CreatedUtc).Take(Math.Clamp(limit, 1, 500)).ToList();
        }
    }

    public int PurgeExpiredAudit(int retentionDays)
    {
        lock (_auditLock)
        {
            Directory.CreateDirectory(AuditDir);
            var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(retentionDays, 1, 3650)).ToUnixTimeSeconds();
            var removed = 0;
            foreach (var file in Directory.GetFiles(AuditDir, "*.json"))
            {
                var entry = AtomicRead<BpAuditLogEntry>(file);
                if (entry is null || entry.CreatedUtc >= cutoff) continue;
                File.Delete(file);
                removed++;
            }

            return removed;
        }
    }

    /// <summary>
    ///     读取并幂等升级旧审核记录。v1 删除记录没有保存提交 payload，批准时会把 JSON null
    ///     交给要求对象输入的删除 handler；这里用稳定 TargetId 重建原始 { id } 输入。
    /// </summary>
    private BpChangeRequest? ReadAndMigrateChange(string path)
    {
        var change = AtomicRead<BpChangeRequest>(path);
        if (change is null)
        {
            return null;
        }

        var changed = false;
        if (change.SchemaVersion < 2)
        {
            change.SchemaVersion = 2;
            changed = true;
        }

        if (change.Version < 1)
        {
            change.Version = 1;
            changed = true;
        }

        if (change.CommandType.Contains("delete", StringComparison.OrdinalIgnoreCase)
            && change.ProposedPayload is null
            && !string.IsNullOrWhiteSpace(change.TargetId))
        {
            change.ProposedPayload = new Dictionary<string, string> { ["id"] = change.TargetId };
            changed = true;

            // 只恢复由已知 v1 空 payload 缺陷造成的失败；其它业务失败仍由管理员显式处理。
            if (change.Status == "failed"
                && change.FailureMessage?.Contains("target element has type 'Null'", StringComparison.OrdinalIgnoreCase) == true)
            {
                change.Status = "pending";
                change.Reviewer = null;
                change.ReviewedUtc = 0;
                change.FailureCode = null;
                change.FailureMessage = null;
                change.UpdatedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                change.Version++;
                change.History.Add(new BpChangeEvent
                {
                    Type = "recover",
                    Timestamp = change.UpdatedUtc,
                    ActorId = "system",
                    ActorDisplayName = "系统修复",
                    Detail = "恢复旧版删除审核的空 payload",
                });
            }
        }

        if (changed)
        {
            AtomicWrite(path, change);
        }

        return change;
    }

    // ---- 原子 IO ----

    private T? AtomicRead<T>(string path)
    {
        try
        {
            if (!File.Exists(path)) return default;
            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(text, JsonOpts);
        }
        catch (Exception ex)
        {
            // 损坏文件移入隔离区
            try
            {
                Directory.CreateDirectory(QuarantineDir);
                var dest = Path.Combine(QuarantineDir, $"{Path.GetFileName(path)}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
                File.Move(path, dest);
            }
            catch { /* 移动失败不阻塞 */ }

            logger.Error($"BattlePassChangeStore: 读取失败并已隔离 {Path.GetFileName(path)}: {ex.GetType().Name}");
            return default;
        }
    }

    private void AtomicWrite<T>(string path, T value)
    {
        try
        {
            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            var json = JsonSerializer.Serialize(value, JsonOpts);

            // 写入临时文件
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(fs, System.Text.Encoding.UTF8);
                writer.Write(json);
                writer.Flush();
                fs.Flush(flushToDisk: true);
            }

            // 保留 .bak
            if (File.Exists(path))
            {
                var bak = path + ".bak";
                File.Copy(path, bak, overwrite: true);
            }

            // 原子替换
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            logger.Error($"BattlePassChangeStore: 写入失败 {Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}");
            throw; // 上层需要感知写盘失败
        }
    }

    private static string ChangeFilePath(string changeId)
    {
        // 文件名只允许安全字符
        var safe = string.Concat(changeId.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'));
        return Path.Combine(ChangesDir, $"{safe}.json");
    }
}
