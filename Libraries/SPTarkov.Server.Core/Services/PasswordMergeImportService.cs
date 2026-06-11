using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;

namespace SPTarkov.Server.Core.Services;

/// <summary>
///     一次性迁移：把外挂 mod 时代的 user/passwords.json（profileId → SHA256 大写十六进制）
///     合并回 SptProfile.Info.Password。passwords.json 为权威，无条件覆盖存档残值。
///     完成后将文件改名为 passwords.json.bak-merge —— 文件不存在即跳过，天然幂等可重跑。
/// </summary>
[Injectable(TypePriority = OnLoadOrder.SaveCallbacks + 1)]
public class PasswordMergeImportService(
    SaveServer saveServer,
    FileUtil fileUtil,
    JsonUtil jsonUtil,
    ISptLogger<PasswordMergeImportService> logger
) : IOnLoad
{
    protected const string LegacyPasswordsFile = "user/passwords.json";

    public async Task OnLoad()
    {
        if (!fileUtil.FileExists(LegacyPasswordsFile))
        {
            return;
        }

        Dictionary<string, string>? legacyMap;
        try
        {
            legacyMap = jsonUtil.Deserialize<Dictionary<string, string>>(fileUtil.ReadFile(LegacyPasswordsFile));
        }
        catch (Exception ex)
        {
            // 解析失败时保留原文件不动，避免销毁唯一的密码数据
            logger.Error($"[PasswordMerge] 无法解析 {LegacyPasswordsFile}，跳过迁移: {ex.Message}");
            return;
        }

        if (legacyMap is null || legacyMap.Count == 0)
        {
            ArchiveLegacyFile();
            return;
        }

        var profiles = saveServer.GetProfiles();
        var imported = 0;
        var orphaned = 0;

        foreach (var (profileId, hash) in legacyMap)
        {
            if (string.IsNullOrEmpty(hash) || !MongoId.IsValidMongoId(profileId))
            {
                orphaned++;
                logger.Warning($"[PasswordMerge] 条目无效，跳过: {profileId}");
                continue;
            }

            if (!profiles.TryGetValue(new MongoId(profileId), out var profile) || profile.ProfileInfo is null)
            {
                orphaned++;
                logger.Warning($"[PasswordMerge] passwords.json 中的 profile 不存在，跳过: {profileId}");
                continue;
            }

            profile.ProfileInfo.Password = hash;
            imported++;
        }

        if (imported > 0)
        {
            await saveServer.SaveAsync();
        }

        ArchiveLegacyFile();
        logger.Success($"[PasswordMerge] 密码迁移完成: 导入 {imported} 条，跳过 {orphaned} 条；原文件已转存 .bak-merge");
    }

    /// <summary>
    ///     原文件转存为 .bak-merge（已存在同名备份时附加时间戳），随后删除原文件以达成幂等。
    /// </summary>
    protected void ArchiveLegacyFile()
    {
        var backupPath = $"{LegacyPasswordsFile}.bak-merge";
        if (fileUtil.FileExists(backupPath))
        {
            backupPath = $"{LegacyPasswordsFile}.bak-merge-{DateTime.Now:yyyyMMddHHmmss}";
        }

        fileUtil.CopyFile(LegacyPasswordsFile, backupPath);
        fileUtil.DeleteFile(LegacyPasswordsFile);
    }
}
