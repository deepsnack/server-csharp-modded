using Microsoft.AspNetCore.Mvc;

namespace SPTarkov.Server.Core.Utils;

/// <summary>
///     合并包内静态网页的 MVC serve 辅助。
///     取代失效的 *StaticPageAliasPatch（HarmonyX 挂 SPTWeb.UseSptBlazor 的别名补丁在 vanilla 上不生效，
///     参见服务端 Harmony 补丁不可靠的经验）。各模块的页控制器以 catch-all 路由调用本辅助，
///     按相对路径从已解压的页目录返回文件，内置防目录穿越与扩展名→Content-Type 映射。
/// </summary>
public static class StaticPageContent
{
    /// <summary>从 <paramref name="pageDir"/> 返回 <paramref name="relativePath"/> 对应文件；空路径→index.html；越界或不存在→404。</summary>
    public static IActionResult Serve(string pageDir, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            relativePath = "index.html";
        }

        var baseDir = Path.GetFullPath(pageDir);
        var full = Path.GetFullPath(Path.Combine(baseDir, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        // 防目录穿越：解析后的绝对路径必须仍位于 baseDir 之下。
        var prefix = baseDir.EndsWith(Path.DirectorySeparatorChar) ? baseDir : baseDir + Path.DirectorySeparatorChar;
        if (!(full + Path.DirectorySeparatorChar).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(full))
        {
            return new NotFoundResult();
        }

        return new PhysicalFileResult(full, ContentType(full));
    }

    private static string ContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".map" => "application/json; charset=utf-8",
            _ => "application/octet-stream",
        };
}
