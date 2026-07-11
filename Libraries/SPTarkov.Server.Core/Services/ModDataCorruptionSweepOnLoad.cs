using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Utils;

namespace SPTarkov.Server.Core.Services;

/// <summary>
///     启动期扫描 user/mods 下的 JSON 数据文件，隔离被撕裂写（torn write）损坏成以 0x00 开头的坏文件。
///     <para>
///     背景：Windows 上进程被强杀/断电时，NTFS 已扩展文件大小并零填充、但真实数据尚未落盘，
///     留下以 NUL(0x00) 开头的坏 JSON。第三方 mod（如 acidphantasm-botplacementsystem）读到后会抛
///     "'0x00' is an invalid start of a value"，且坏文件反复被读、反复刷屏，对应存档数据也永远读不回。
///     </para>
///     <para>
///     我们自己的 mod 已在读取处内建自愈，但第三方编译 DLL 无法改源码，只能在启动期把坏文件改名移走：
///     下次 mod 读取时文件视为不存在，自然重建默认数据，实现自愈且不再刷屏。
///     </para>
///     <para>
///     检测非常保守：仅隔离"首个非空白字节为 0x00"的 .json 文件。合法 JSON 绝不会以 0x00 开头，
///     正常配置文件更不可能，因此绝不误伤有效数据。仅探测文件头部若干字节，不受大文件影响。
///     </para>
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PreSptModLoader)]
public class ModDataCorruptionSweepOnLoad(ISptLogger<ModDataCorruptionSweepOnLoad> logger) : IOnLoad
{
    // 相对服务端工作目录，与 ProfileDataService 的 "user/profileData/" 保持一致的相对定位方式。
    private const string ModsBasePath = "user/mods";

    public Task OnLoad()
    {
        try
        {
            if (!Directory.Exists(ModsBasePath))
            {
                return Task.CompletedTask;
            }

            var scanned = 0;
            var quarantined = 0;

            foreach (var filePath in Directory.EnumerateFiles(ModsBasePath, "*.json", SearchOption.AllDirectories))
            {
                scanned++;
                try
                {
                    if (!StartsWithNul(filePath))
                    {
                        continue;
                    }

                    var backupPath = $"{filePath}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
                    File.Move(filePath, backupPath, overwrite: true);
                    quarantined++;
                    logger.Warning(
                        $"[ModDataCorruptionSweep] 损坏的 mod 数据文件 '{filePath}' 已隔离为 '{backupPath}'（内容以 0x00 开头，疑似写入中途崩溃）；mod 将重建默认数据。"
                    );
                }
                catch (Exception ex)
                {
                    // 单个文件失败（占用/权限）不阻断整体扫描，也绝不冒泡影响启动。
                    logger.Error($"[ModDataCorruptionSweep] 处理 '{filePath}' 失败：{ex.Message}");
                }
            }

            if (quarantined > 0)
            {
                logger.Success(
                    $"[ModDataCorruptionSweep] 扫描 {scanned} 个 mod JSON 文件，隔离 {quarantined} 个损坏文件。"
                );
            }
        }
        catch (Exception ex)
        {
            // 清扫是尽力而为的保护机制，任何异常都不得阻断服务端启动。
            logger.Error($"[ModDataCorruptionSweep] 扫描 mod 数据目录失败：{ex.Message}");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    ///     仅读取文件头部若干字节，跳过 UTF-8 BOM 与空白字符后，判断首个有效字节是否为 NUL(0x00)。
    ///     合法 JSON 首字符必为 { [ " 数字 t f n 之一，绝不会是 0x00，故此判定不会误伤有效文件。
    /// </summary>
    private static bool StartsWithNul(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        Span<byte> head = stackalloc byte[16];
        var read = fs.Read(head);
        if (read == 0)
        {
            // 0 字节空文件不是本机制目标（不会抛 "0x00" 错误），交给各 mod 自行处理。
            return false;
        }

        var index = 0;

        // 跳过 UTF-8 BOM（EF BB BF）
        if (read >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
        {
            index = 3;
        }

        // 跳过前导空白（空格/制表/回车/换行）
        while (index < read && (head[index] == 0x20 || head[index] == 0x09 || head[index] == 0x0D || head[index] == 0x0A))
        {
            index++;
        }

        if (index >= read)
        {
            // 头部全是 BOM/空白，无法判定为损坏，保守放行。
            return false;
        }

        return head[index] == 0x00;
    }
}
