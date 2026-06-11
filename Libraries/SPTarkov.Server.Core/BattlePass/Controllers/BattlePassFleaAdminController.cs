using Microsoft.AspNetCore.Mvc;
using SPTarkov.Server.Core.BattlePass.ItemControl;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.DI.Annotations;

namespace SPTarkov.Server.Core.BattlePass.Controllers;

/// <summary>
///     跳蚤黑名单接管管理端 API（X-Admin-Token）：读写 flea-control 配置（保存即应用到 RagfairConfig），
///     物品搜索复用 <see cref="ItemSearchService"/>。
/// </summary>
[Injectable]
[ApiController]
[Route("battlepass/api/admin/flea")]
public class BattlePassFleaAdminController(
    ItemSearchService searchService,
    FleaControlSync fleaSync
)
{
    private static bool Auth(string? token) => WebRegisterController.IsAdminAuthorized(token);

    [HttpGet("config")]
    public object GetConfig([FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        return new { success = true, config = BattlePassStore.GetFleaControl() };
    }

    [HttpPost("config")]
    public object SaveConfig([FromBody] BpFleaControl config, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        BattlePassStore.SaveFleaControl(config);
        fleaSync.Apply();
        return new { success = true };
    }

    [HttpGet("search")]
    public object Search(
        [FromQuery] string? q,
        [FromQuery] string? source,
        [FromHeader(Name = "X-Admin-Token")] string? token = null
    )
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        return new { success = true, items = searchService.Search(q, source) };
    }
}
