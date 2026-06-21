using System.Text.Json.Serialization;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace SPTarkov.Server.Core.Services;

/// <summary>
///     安全箱守卫（原 SPT-ProfileCore SecureContainerGuard 内置化）：
///     战局结算重建库存前备份服务端安全箱内容；若客户端上报"战前有物品、战后箱空/缺失"的异常，
///     恢复服务端备份，防止客户端异常（崩溃/篡改）吞掉安全箱物品。
///     开关：SPT_Data/securecontainerguard/config.json（enabled，默认开，与 mod 版路径一致）。
/// </summary>
[Injectable(InjectionType.Singleton)]
public class SecureContainerGuardService(FileUtil fileUtil, JsonUtil jsonUtil, ISptLogger<SecureContainerGuardService> logger)
{
    protected static readonly string ConfigPath = System.IO.Path.Combine(
        Directory.GetCurrentDirectory(),
        "SPT_Data",
        "securecontainerguard",
        "config.json"
    );

    protected SecureContainerGuardConfig? config;

    public bool Enabled => (config ??= LoadConfig()).Enabled;

    /// <summary>
    ///     结算重建库存前备份安全箱（容器项+全部子项）；箱为空或不存在返回 null。
    /// </summary>
    public List<Item>? BackupSecureContainer(PmcData serverProfile)
    {
        if (!Enabled)
        {
            return null;
        }

        var secContainer = serverProfile.Inventory?.Items?.FirstOrDefault(x => x.SlotId == "SecuredContainer");
        if (secContainer is null)
        {
            return null;
        }

        var backup = serverProfile.Inventory!.Items!.GetItemWithChildren(secContainer.Id);
        return backup.Count > 1 ? backup : null;
    }

    /// <summary>
    ///     库存重建完成后检测异常（战前有物品、客户端上报空箱），命中则恢复备份。
    /// </summary>
    public void RestoreIfAnomalous(MongoId sessionId, PmcData serverProfile, PmcData postRaidProfile, List<Item>? backup)
    {
        if (backup is null)
        {
            return;
        }

        var clientItems = postRaidProfile.Inventory?.Items;
        var clientContentsCount = clientItems is not null
            ? clientItems.Count(x => x.ItemIsInsideContainer("SecuredContainer", clientItems))
            : 0;

        var backupContentsCount = backup.Count - 1; // 不含容器本体

        if (backupContentsCount <= 0 || clientContentsCount > 0)
        {
            return;
        }

        var serverItems = serverProfile.Inventory?.Items;
        if (serverItems is null)
        {
            return;
        }

        // 移除客户端上报的安全箱内容（如有），再恢复战前备份
        var currentSecContainer = serverItems.FirstOrDefault(x => x.SlotId == "SecuredContainer");
        if (currentSecContainer is not null)
        {
            var toRemove = serverItems.GetItemWithChildren(currentSecContainer.Id).Select(x => x.Id).ToHashSet();
            serverItems.RemoveAll(x => toRemove.Contains(x.Id));
        }

        serverItems.AddRange(backup);

        logger.Warning(
            $"[SecureContainerGuard] sessionId={sessionId}: anomaly detected "
                + $"(pre-raid {backupContentsCount} item(s) in secure container, client reported 0). Server backup restored."
        );
    }

    protected SecureContainerGuardConfig LoadConfig()
    {
        try
        {
            if (!fileUtil.FileExists(ConfigPath))
            {
                var template = new SecureContainerGuardConfig();
                fileUtil.WriteFile(ConfigPath, jsonUtil.Serialize(template, true) ?? "{}");
                return template;
            }

            return jsonUtil.Deserialize<SecureContainerGuardConfig>(fileUtil.ReadFile(ConfigPath)) ?? new SecureContainerGuardConfig();
        }
        catch
        {
            return new SecureContainerGuardConfig();
        }
    }
}

public record SecureContainerGuardConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}
