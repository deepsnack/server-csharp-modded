using Microsoft.AspNetCore.Mvc;
using SPTarkov.Server.Core.Utils;
using SPTarkov.DI.Annotations;

namespace SPTarkov.Server.Core.Controllers;

/// <summary>
///     注册页静态页 MVC serve：/register、/register/index.html、/register/admin/index.html 等。
///     取代失效的 RegisterStaticPageAliasPatch。/register/api/* 由 <see cref="WebRegisterController"/>
///     处理——字面量路由优先级高于本 catch-all，不会被遮蔽。
/// </summary>
[Injectable]
[ApiController]
// 注册页/管理页为纯静态文件，PhysicalFileResult 默认只发 Last-Modified、无 Cache-Control，
// 浏览器据此做启发式缓存，导致部署新版后仍长时间服旧 JS/HTML（表现为下拉空白、搜索失效等）。
// 统一标记不缓存：每次都向服务器取最新页面，部署即时生效。页面很小，开销可忽略。
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class RegisterPageController
{
    // 页面随 Assets 工程的 SPT_Data/webregister/page/ 输出到运行目录（原 mod 嵌入资源解压机制不再需要）
    private static readonly string PageDir = Path.Combine(Directory.GetCurrentDirectory(), "SPT_Data", "webregister", "page");

    [HttpGet("register")]
    public IActionResult Root() => StaticPageContent.Serve(PageDir, "index.html");

    [HttpGet("register/{**path}")]
    public IActionResult Page(string? path) => StaticPageContent.Serve(PageDir, path);
}
