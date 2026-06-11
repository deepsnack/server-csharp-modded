using System.Collections.Concurrent;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Utils;

namespace SPTarkov.Server.Core.Services;

/// <summary>
///     登录时间侧存储（原 SPT-ProfileCore LastLoginStore 内置化，由静态类改为 DI 单例）。
///     键 profileId、值 Unix 秒，文件路径与 mod 版一致（SPT_Data/profilecleanup/lastlogin.json），
///     保证已有生产数据无缝沿用。登录成功时由 Launcher V1/V2 控制器内联记录，离线清理据此判断活跃度。
/// </summary>
[Injectable(InjectionType.Singleton)]
public class LastLoginService(FileUtil fileUtil, JsonUtil jsonUtil)
{
    protected static readonly string StorePath = Path.Combine(
        Directory.GetCurrentDirectory(),
        "SPT_Data",
        "profilecleanup",
        "lastlogin.json"
    );

    protected readonly object gate = new();
    protected ConcurrentDictionary<string, long>? map;

    protected ConcurrentDictionary<string, long> Map
    {
        get
        {
            if (map is not null)
            {
                return map;
            }

            lock (gate)
            {
                map ??= Load();
            }

            return map;
        }
    }

    protected ConcurrentDictionary<string, long> Load()
    {
        try
        {
            if (fileUtil.FileExists(StorePath))
            {
                var dict = jsonUtil.Deserialize<Dictionary<string, long>>(fileUtil.ReadFile(StorePath));
                if (dict is not null)
                {
                    return new ConcurrentDictionary<string, long>(dict);
                }
            }
        }
        catch
        {
            // 损坏当作空表，避免阻断登录
        }

        return new ConcurrentDictionary<string, long>();
    }

    protected void Save()
    {
        lock (gate)
        {
            try
            {
                fileUtil.WriteFile(StorePath, jsonUtil.Serialize(new Dictionary<string, long>(Map), true) ?? "{}");
            }
            catch
            {
                // 写盘失败不应中断登录
            }
        }
    }

    /// <summary>登录成功时记录当前时间戳。</summary>
    public void Record(MongoId sessionId)
    {
        Map[sessionId.ToString()] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Save();
    }

    /// <summary>取上次登录 Unix 秒；从未记录返回 null。</summary>
    public long? Get(MongoId sessionId)
    {
        return Map.TryGetValue(sessionId.ToString(), out var t) ? t : null;
    }
}
