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
public class BattlePassPageController
{
    // 页面随 Assets 工程的 SPT_Data/battlepass/page/ 输出到运行目录（原 mod 嵌入资源解压机制不再需要）
    private static readonly string PageDir = Path.Combine(Directory.GetCurrentDirectory(), "SPT_Data", "battlepass", "page");

    [HttpGet("battlepass")]
    public IActionResult Root() => StaticPageContent.Serve(PageDir, "index.html");

    [HttpGet("battlepass/{**path}")]
    public IActionResult Page(string? path) => StaticPageContent.Serve(PageDir, path);
}
