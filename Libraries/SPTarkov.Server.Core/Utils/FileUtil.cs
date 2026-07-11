using System.Text;
using SPTarkov.DI.Annotations;

namespace SPTarkov.Server.Core.Utils;

[Injectable]
public class FileUtil
{
    protected const string _modBasePath = "user/mods/";

    public List<string> GetFiles(string path, bool recursive = false, string searchPattern = "*")
    {
        var files = new List<string>(Directory.GetFiles(path, searchPattern));

        if (recursive)
        {
            files.AddRange(Directory.GetDirectories(path).SelectMany(d => GetFiles(d, recursive, searchPattern)));
        }

        return files;
    }

    public string[] GetDirectories(string path)
    {
        return Directory.GetDirectories(path);
    }

    public string GetFileExtension(string path)
    {
        return Path.GetExtension(path).Replace(".", "");
    }

    public string GetFileNameAndExtension(string path)
    {
        return Path.GetFileName(path);
    }

    public string StripExtension(string path, bool keepPath = false)
    {
        if (keepPath)
        {
            return path.StartsWith(".") ? path.Split('.')[1] : path.Split('.').First();
        }

        return Path.GetFileNameWithoutExtension(path);
    }

    public bool DirectoryExists(string path)
    {
        return Directory.Exists(path);
    }

    public DirectoryInfo CreateDirectory(string path)
    {
        return Directory.CreateDirectory(path);
    }

    public bool FileExists(string path)
    {
        return File.Exists(path);
    }

    public string ReadFile(string path)
    {
        return File.ReadAllText(path);
    }

    public async Task<string> ReadFileAsync(string path)
    {
        return await File.ReadAllTextAsync(path);
    }

    public async Task<byte[]> ReadFileAsBytesAsync(string path)
    {
        return await File.ReadAllBytesAsync(path);
    }

    public void WriteFile(string filePath, string fileContent)
    {
        if (!DirectoryExists(Path.GetDirectoryName(filePath)))
        {
            CreateDirectory(Path.GetDirectoryName(filePath));
        }

        if (!FileExists(filePath))
        {
            CreateFile(filePath);
        }

        File.WriteAllText(filePath, fileContent);
    }

    public void WriteFile(string filePath, byte[] fileContent)
    {
        if (!FileExists(filePath))
        {
            CreateFile(filePath);
        }

        File.WriteAllBytes(filePath, fileContent);
    }

    /// <summary>
    ///     同步原子写：先写唯一 tmp 文件并 flush 落盘，再原子替换目标。
    ///     防止写入中途进程崩溃/断电留下 0 字节或 NUL 填充的半截文件（会导致后续反序列化以 0x00 开头而抛异常）。
    ///     用于 <see cref="Services.Mod.ProfileDataService"/> 等在请求链路上同步保存的场景。
    /// </summary>
    public void WriteFileAtomic(string filePath, string fileContent)
    {
        var directoryPath = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directoryPath) && !Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        var tempFilePath = Path.Combine(directoryPath ?? string.Empty, $"{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var fs = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(fileContent);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true); // 确保内容真正落盘后再替换
            }

            File.Move(tempFilePath, filePath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempFilePath))
            {
                try
                {
                    File.Delete(tempFilePath);
                }
                catch { }
            }
            throw;
        }
    }

    public async Task WriteFileAsync(string filePath, string fileContent)
    {
        var bytes = Encoding.UTF8.GetBytes(fileContent);
        await WriteFileAsync(filePath, bytes);
    }

    /// <summary>
    /// Writes a file atomically by first writing to a temporary file, then replacing the original.
    /// This prevents corruption if the write operation fails or is interrupted.
    /// </summary>
    public async Task WriteFileAsync(string filePath, byte[] fileContent)
    {
        var directoryPath = Path.GetDirectoryName(filePath);

        if (!string.IsNullOrEmpty(directoryPath) && !Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        // 唯一 GUID 临时文件名，避免并发写同一目标时 .bak 互踩（原 SPT-Performance FileUtilRobust 内联）
        var tempFilePath = Path.Combine(directoryPath ?? string.Empty, $"{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (
                var fs = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true)
            )
            {
                await fs.WriteAsync(fileContent);

                // We flush here so we can be sure it's immediately committed to disk
                await fs.FlushAsync();
                fs.Flush(true);
            }

            // Overwrite over the old file; Windows 下其它进程短暂持有读句柄会使单次 rename 失败，带重试
            await MoveFileWithRetriesAsync(tempFilePath, filePath);
        }
        catch
        {
            if (File.Exists(tempFilePath))
            {
                try
                {
                    File.Delete(tempFilePath);
                }
                catch { }
            }
            throw;
        }
    }

    /// <summary>
    ///     原子覆盖 Move，IO/权限类瞬时失败重试（最多 20 次、间隔 250ms）。
    /// </summary>
    protected static async Task MoveFileWithRetriesAsync(string sourceFilePath, string destinationFilePath)
    {
        const int maxAttempts = 20;
        var retryDelay = TimeSpan.FromMilliseconds(250);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(sourceFilePath, destinationFilePath, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < maxAttempts)
            {
                await Task.Delay(retryDelay);
            }
        }
    }

    private void CreateFile(string filePath)
    {
        var stream = File.Create(filePath);
        stream.Close();
    }

    public bool DeleteFile(string filePath)
    {
        if (!FileExists(filePath))
        {
            return false;
        }

        File.Delete(filePath);
        return true;
    }

    /// <summary>
    ///     Copy a file from one path to another
    /// </summary>
    /// <param name="copyFromPath">Source file to copy from</param>
    /// <param name="destinationFilePath"></param>
    /// <param name="overwrite">Should destination file be overwritten</param>
    public bool CopyFile(string copyFromPath, string destinationFilePath, bool overwrite = false)
    {
        // Check it exists first
        if (!FileExists(copyFromPath))
        {
            return false;
        }

        // Ensure dir exists
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath));

        // Copy the file
        File.Copy(copyFromPath, destinationFilePath, overwrite);
        return true;
    }

    /// <summary>
    ///     Delete a directory, must be empty unless 'deleteContent' is set to 'true'
    /// </summary>
    /// <param name="directory"></param>
    /// <param name="deleteContent"></param>
    public void DeleteDirectory(string directory, bool deleteContent = false)
    {
        Directory.Delete(directory, deleteContent);
    }

    public string GetModPath(string modName)
    {
        return Path.Combine(_modBasePath, modName);
    }
}
