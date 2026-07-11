using Microsoft.AspNetCore.Mvc;
using SPTarkov.Server.Core.Utils;
using SPTarkov.DI.Annotations;

namespace SPTarkov.Server.Core.BattlePass.Controllers;

/// <summary>
///     通行证静态页 MVC serve：/battlepass、/battlepass/index.html、/battlepass/admin/index.html 等。
///     取代失效的 BattlePassStaticPageAliasPatch。/battlepass/api/* 由 <see cref="BattlePassController"/>
///     处理——字面量路由优先级高于本 catch-all，不会被遮蔽。
/// </summary>
[Injectable]
[ApiController]
// 通行证页/后台页为纯静态文件，PhysicalFileResult 默认只发 Last-Modified、无 Cache-Control，
// 浏览器据此做启发式缓存，导致部署新版后仍长时间服旧 JS/HTML（如旧 script.js 仍跳错误的
// /battlepass/page/admin/index.html 双 page 路径 → 404）。统一标记不缓存：每次都取最新页面，部署即时生效。
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class BattlePassPageController
{
    // 页面随 Assets 工程的 SPT_Data/battlepass/page/ 输出到运行目录（原 mod 嵌入资源解压机制不再需要）
    private static readonly string PageDir = Path.Combine(Directory.GetCurrentDirectory(), "SPT_Data", "battlepass", "page");

    [HttpGet("battlepass")]
    public IActionResult Root() => StaticPageContent.Serve(PageDir, "index.html");

    [HttpGet("battlepass/{**path}")]
    public IActionResult Page(string? path) => StaticPageContent.Serve(PageDir, path);
}
