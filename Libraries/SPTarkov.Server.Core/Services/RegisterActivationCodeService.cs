using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace SPTarkov.Server.Core.Services;

/// <summary>
///     注册激活码（A2/N2）：管理员创建绑定特定版本（含对普通用户隐藏的版本）的激活码分发给用户；
///     用户注册时输码锁定该版本，注册成功后码原子失效。与 BattlePass 的发奖兑换码无关。
///     使用日志记录每码的创建/兑换/作废事件，支持导出、删除、保留期设置（0=不限时，默认）。
///     存储：SPT_Data/webregister/activation_codes.json + activation_log.json。
/// </summary>
[Injectable(InjectionType.Singleton)]
public class RegisterActivationCodeService(FileUtil fileUtil, JsonUtil jsonUtil, ISptLogger<RegisterActivationCodeService> logger)
    : IOnUpdate
{
    protected static readonly string CodesPath = System.IO.Path.Combine(
        Directory.GetCurrentDirectory(),
        "SPT_Data",
        "webregister",
        "activation_codes.json"
    );

    protected static readonly string LogPath = System.IO.Path.Combine(
        Directory.GetCurrentDirectory(),
        "SPT_Data",
        "webregister",
        "activation_log.json"
    );

    protected readonly object gate = new();
    protected List<RegisterActivationCode>? codes;
    protected ActivationLogFile? log;

    // ---- 码生命周期 ----

    public List<RegisterActivationCode> ListCodes()
    {
        lock (gate)
        {
            return [.. Codes];
        }
    }

    /// <summary>批量创建：返回新码列表。码格式 XXXX-XXXX-XXXX（去易混淆字符）。</summary>
    public List<string> CreateCodes(string edition, int count, string? note, DateTime? expiresAt)
    {
        lock (gate)
        {
            var created = new List<string>();
            for (var i = 0; i < Math.Clamp(count, 1, 500); i++)
            {
                string code;
                do
                {
                    code = GenerateCode();
                } while (Codes.Any(c => c.Code == code));

                Codes.Add(
                    new RegisterActivationCode
                    {
                        Code = code,
                        Edition = edition,
                        Note = note,
                        CreatedAt = DateTime.UtcNow,
                        ExpiresAt = expiresAt,
                        Status = ActivationCodeStatus.Unused,
                    }
                );
                created.Add(code);
                AppendLog("created", code, edition, null, null);
            }

            SaveCodes();
            SaveLog();
            logger.Success($"[ActivationCode] 创建 {created.Count} 个激活码（版本: {edition}）");
            return created;
        }
    }

    /// <summary>作废未使用的码。</summary>
    public bool RevokeCode(string code)
    {
        lock (gate)
        {
            var entry = Codes.FirstOrDefault(c => c.Code == code);
            if (entry is null || entry.Status != ActivationCodeStatus.Unused)
            {
                return false;
            }

            entry.Status = ActivationCodeStatus.Revoked;
            SaveCodes();
            AppendLog("revoked", code, entry.Edition, null, null);
            SaveLog();
            return true;
        }
    }

    /// <summary>校验码当前是否可用（不消耗）。返回 (valid, edition, message)。</summary>
    public (bool Valid, string? Edition, string Message) ValidateCode(string code)
    {
        lock (gate)
        {
            var entry = Codes.FirstOrDefault(c => c.Code == Normalize(code));
            if (entry is null)
            {
                return (false, null, "激活码不存在");
            }

            if (entry.Status == ActivationCodeStatus.Used)
            {
                return (false, null, "激活码已被使用");
            }

            if (entry.Status == ActivationCodeStatus.Revoked)
            {
                return (false, null, "激活码已作废");
            }

            if (entry.ExpiresAt.HasValue && entry.ExpiresAt.Value < DateTime.UtcNow)
            {
                return (false, null, "激活码已过期");
            }

            return (true, entry.Edition, "激活码有效");
        }
    }

    /// <summary>
    ///     原子占用：校验通过即标记已用（带邮箱/用户名/时间）。注册失败时调用 <see cref="ReleaseCode"/> 回滚。
    /// </summary>
    public (bool Ok, string? Edition, string Message) TryRedeem(string code, string email, string username)
    {
        lock (gate)
        {
            var normalized = Normalize(code);
            var (valid, edition, message) = ValidateCode(normalized);
            if (!valid)
            {
                return (false, null, message);
            }

            var entry = Codes.First(c => c.Code == normalized);
            entry.Status = ActivationCodeStatus.Used;
            entry.UsedByEmail = email;
            entry.UsedByUsername = username;
            entry.UsedAt = DateTime.UtcNow;
            SaveCodes();
            AppendLog("used", normalized, entry.Edition, email, username);
            SaveLog();
            logger.Info($"[ActivationCode] 激活码 {normalized} 已被 {username} ({email}) 使用，版本 {entry.Edition}");
            return (true, edition, "OK");
        }
    }

    /// <summary>注册失败回滚：把刚占用的码恢复为未使用。</summary>
    public void ReleaseCode(string code)
    {
        lock (gate)
        {
            var entry = Codes.FirstOrDefault(c => c.Code == Normalize(code) && c.Status == ActivationCodeStatus.Used);
            if (entry is null)
            {
                return;
            }

            entry.Status = ActivationCodeStatus.Unused;
            entry.UsedByEmail = null;
            entry.UsedByUsername = null;
            entry.UsedAt = null;
            SaveCodes();
            AppendLog("released", entry.Code, entry.Edition, null, null);
            SaveLog();
        }
    }

    // ---- 日志 ----

    public List<ActivationLogEntry> GetLogs()
    {
        lock (gate)
        {
            return [.. Log.Entries];
        }
    }

    public string ExportLogsCsv()
    {
        lock (gate)
        {
            var sb = new StringBuilder();
            sb.AppendLine("time,event,code,edition,email,username");
            foreach (var e in Log.Entries)
            {
                sb.AppendLine($"{e.Time:O},{e.Event},{e.Code},{Csv(e.Edition)},{Csv(e.Email)},{Csv(e.Username)}");
            }

            return sb.ToString();
        }
    }

    /// <summary>删除日志；before 为空删全部，否则删该时间之前的条目。返回删除数。</summary>
    public int ClearLogs(DateTime? before)
    {
        lock (gate)
        {
            var removed = before.HasValue ? Log.Entries.RemoveAll(e => e.Time < before.Value) : Log.Entries.Count;
            if (!before.HasValue)
            {
                Log.Entries.Clear();
            }

            SaveLog();
            return removed;
        }
    }

    /// <summary>日志保留天数；0 = 不限时保存（默认）。</summary>
    public int RetentionDays
    {
        get
        {
            lock (gate)
            {
                return Log.RetentionDays;
            }
        }
        set
        {
            lock (gate)
            {
                Log.RetentionDays = Math.Max(0, value);
                SaveLog();
            }
        }
    }

    /// <summary>定期按保留期清理过期日志（每小时检查一次；0 = 不清理）。</summary>
    public Task<bool> OnUpdate(long secondsSinceLastRun)
    {
        if (secondsSinceLastRun < 3600)
        {
            return Task.FromResult(false);
        }

        lock (gate)
        {
            if (Log.RetentionDays <= 0)
            {
                return Task.FromResult(false);
            }

            var cutoff = DateTime.UtcNow.AddDays(-Log.RetentionDays);
            var removed = Log.Entries.RemoveAll(e => e.Time < cutoff);
            if (removed > 0)
            {
                SaveLog();
                logger.Info($"[ActivationCode] 按保留期({Log.RetentionDays}天)清理日志 {removed} 条");
            }
        }

        return Task.FromResult(true);
    }

    // ---- 内部 ----

    protected List<RegisterActivationCode> Codes => codes ??= LoadJson<List<RegisterActivationCode>>(CodesPath) ?? [];

    protected ActivationLogFile Log => log ??= LoadJson<ActivationLogFile>(LogPath) ?? new ActivationLogFile();

    protected void AppendLog(string evt, string code, string? edition, string? email, string? username)
    {
        Log.Entries.Add(
            new ActivationLogEntry
            {
                Time = DateTime.UtcNow,
                Event = evt,
                Code = code,
                Edition = edition,
                Email = email,
                Username = username,
            }
        );
    }

    protected void SaveCodes()
    {
        fileUtil.WriteFile(CodesPath, jsonUtil.Serialize(Codes, true) ?? "[]");
    }

    protected void SaveLog()
    {
        fileUtil.WriteFile(LogPath, jsonUtil.Serialize(Log, true) ?? "{}");
    }

    protected T? LoadJson<T>(string path)
        where T : class
    {
        try
        {
            return fileUtil.FileExists(path) ? jsonUtil.Deserialize<T>(fileUtil.ReadFile(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    protected static string Normalize(string code)
    {
        return code.Trim().ToUpperInvariant();
    }

    protected static string GenerateCode()
    {
        // 去除易混淆字符（0/O、1/I/L）的字母数字集
        const string alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
        var bytes = RandomNumberGenerator.GetBytes(12);
        var sb = new StringBuilder(14);
        for (var i = 0; i < 12; i++)
        {
            if (i is 4 or 8)
            {
                sb.Append('-');
            }

            sb.Append(alphabet[bytes[i] % alphabet.Length]);
        }

        return sb.ToString();
    }

    protected static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Contains(',') || value.Contains('"') ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}

public enum ActivationCodeStatus
{
    Unused,
    Used,
    Revoked,
}

public record RegisterActivationCode
{
    [JsonPropertyName("code")]
    public required string Code { get; set; }

    [JsonPropertyName("edition")]
    public required string Edition { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTime? ExpiresAt { get; set; }

    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ActivationCodeStatus Status { get; set; }

    [JsonPropertyName("usedByEmail")]
    public string? UsedByEmail { get; set; }

    [JsonPropertyName("usedByUsername")]
    public string? UsedByUsername { get; set; }

    [JsonPropertyName("usedAt")]
    public DateTime? UsedAt { get; set; }
}

public record ActivationLogEntry
{
    [JsonPropertyName("time")]
    public DateTime Time { get; set; }

    [JsonPropertyName("event")]
    public required string Event { get; set; }

    [JsonPropertyName("code")]
    public required string Code { get; set; }

    [JsonPropertyName("edition")]
    public string? Edition { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }
}

public class ActivationLogFile
{
    /// <summary>日志保留天数；0 = 不限时保存（默认）。</summary>
    [JsonPropertyName("retentionDays")]
    public int RetentionDays { get; set; }

    [JsonPropertyName("entries")]
    public List<ActivationLogEntry> Entries { get; set; } = [];
}
