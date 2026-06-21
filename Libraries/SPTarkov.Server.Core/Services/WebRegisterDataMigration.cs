using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace SPTarkov.Server.Core.Services;

/// <summary>
///     一次性数据路径迁移：树内旧版网页注册把邮箱数据放在 SPT_Data/database/ 下
///     （与上游数据库目录语义冲突），统一搬到 SPT_Data/webregister/（mod 版路径，生产数据所在）。
///     目标文件已存在时不覆盖（webregister/ 数据更新），仅把旧文件转存 .bak-merge；幂等可重跑。
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Database + 1)]
public class WebRegisterDataMigration(FileUtil fileUtil, ISptLogger<WebRegisterDataMigration> logger) : IOnLoad
{
    public Task OnLoad()
    {
        var oldDir = Path.Combine(Directory.GetCurrentDirectory(), "SPT_Data", "database");
        var newDir = Path.Combine(Directory.GetCurrentDirectory(), "SPT_Data", "webregister");

        foreach (var name in new[] { "registered_emails.json", "email_mapping.json" })
        {
            var oldPath = Path.Combine(oldDir, name);
            if (!fileUtil.FileExists(oldPath))
            {
                continue;
            }

            var newPath = Path.Combine(newDir, name);
            if (!fileUtil.FileExists(newPath))
            {
                // webregister/ 无同名文件：直接搬运
                fileUtil.CopyFile(oldPath, newPath);
                logger.Success($"[WebRegister] 邮箱数据已迁移: {name} -> SPT_Data/webregister/");
            }
            else
            {
                logger.Warning($"[WebRegister] {name} 在新旧路径都存在，保留 webregister/ 版本，旧文件转存 .bak-merge");
            }

            fileUtil.CopyFile(oldPath, $"{oldPath}.bak-merge", true);
            fileUtil.DeleteFile(oldPath);
        }

        return Task.CompletedTask;
    }
}
