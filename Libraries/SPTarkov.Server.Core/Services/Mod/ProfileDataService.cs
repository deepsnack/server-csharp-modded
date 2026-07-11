using System.Collections.Concurrent;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace SPTarkov.Server.Core.Services.Mod;

[Injectable(InjectionType.Singleton)]
public class ProfileDataService(ISptLogger<ProfileDataService> logger, FileUtil fileUtil, JsonUtil jsonUtil)
{
    protected const string ProfileDataFilepath = "user/profileData/";
    private readonly ConcurrentDictionary<string, object> _profileDataCache = new();

    /// <summary>
    /// Check if a specfici mod file exists for a profile
    /// </summary>
    /// <param name="profileId">Profile to look up</param>
    /// <param name="modKey">Name of json file to look up</param>
    public bool ProfileDataExists(string profileId, string modKey)
    {
        return fileUtil.FileExists(Path.Combine(ProfileDataFilepath, profileId, $"{modKey}.json"));
    }

    public T? GetProfileData<T>(string profileId, string modKey)
    {
        var profileDataKey = GetCacheKey(profileId, modKey);
        if (!_profileDataCache.TryGetValue(profileDataKey, out var value))
        {
            if (ProfileDataExists(profileId, modKey))
            {
                var filePath = Path.Combine(ProfileDataFilepath, profileId, $"{modKey}.json");
                try
                {
                    value = jsonUtil.Deserialize<T>(fileUtil.ReadFile(filePath));
                }
                catch (Exception ex)
                {
                    // 文件损坏（如写入中途崩溃留下 0 字节 / NUL 填充，反序列化以 0x00 开头抛异常）。
                    // 若不处理，异常会一路冒泡把承载它的请求（如 /client/match/local/end 战局结算）打成 Fatal，
                    // 且坏文件反复被读、反复刷屏。此处将坏文件备份改名移走并降级为 null：
                    // 下次 ProfileDataExists 返回 false，mod 自然重建默认数据，实现自愈且不再刷屏。
                    QuarantineCorruptFile(filePath, ex);
                    return default;
                }

                if (value != null)
                {
                    _profileDataCache[profileDataKey] = value;
                }
            }
            else
            {
                value = null;
            }
        }

        return (T?)value;
    }

    /// <summary>
    ///     将损坏的 mod 数据文件改名为 .corrupt 备份移走，使后续读取视其为不存在从而停止刷屏并触发 mod 重建。
    /// </summary>
    private void QuarantineCorruptFile(string filePath, Exception cause)
    {
        try
        {
            var backupPath = $"{filePath}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(filePath, backupPath, overwrite: true);
            logger.Warning($"Corrupt mod profile data '{filePath}' quarantined to '{backupPath}' ({cause.Message}); mod will rebuild defaults.");
        }
        catch (Exception moveEx)
        {
            logger.Error($"Failed to quarantine corrupt mod profile data '{filePath}': {moveEx.Message}");
        }
    }

    public void SaveProfileData<T>(string profileId, string modKey, T profileData)
    {
        ArgumentNullException.ThrowIfNull(profileData);

        var data =
            jsonUtil.Serialize(profileData, profileData.GetType(), true)
            ?? throw new Exception("The profile data when serialized resulted in a null value");

        _profileDataCache[GetCacheKey(profileId, modKey)] = profileData;

        // 原子写，防止写入中途崩溃产生 0 字节 / 半截文件，进而在下次读取时抛反序列化异常。
        fileUtil.WriteFileAtomic(Path.Combine(ProfileDataFilepath, profileId, $"{modKey}.json"), data);
    }

    /// <summary>
    /// Clear all data for a profile
    /// </summary>
    /// <param name="profileId">Id of profile to delete files for</param>
    public void ClearProfileData(string profileId)
    {
        if (!fileUtil.DirectoryExists(Path.Combine(ProfileDataFilepath, profileId)))
        {
            return;
        }

        var profileFiles = fileUtil.GetFiles(Path.Combine(ProfileDataFilepath, profileId));
        foreach (var filepPath in profileFiles)
        {
            fileUtil.DeleteFile(filepPath);
        }

        var keysInCacheToRemove = _profileDataCache.Keys.Where(key => key.StartsWith($"{profileId}:")).ToList(); // ToList so we can iterate over results without modifying collection

        foreach (var key in keysInCacheToRemove)
        {
            _profileDataCache.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Get the cache key in specific format
    /// </summary>
    protected string GetCacheKey(string profileId, string modKey)
    {
        return $"{profileId}:{modKey}";
    }
}
