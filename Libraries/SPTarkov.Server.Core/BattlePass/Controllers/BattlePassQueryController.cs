using Microsoft.AspNetCore.Mvc;
using SPTarkov.Server.Core.BattlePass.ItemControl;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.DI.Annotations;

namespace SPTarkov.Server.Core.BattlePass.Controllers;

/// <summary>
///     通行证统一数据查询接口（约定：各管理模块图形化检索物品/任务都走这里，响应形状统一，杜绝各页各写一套）。
///     鉴权复用 WebRegister admin 域（X-Admin-Token / Portal SSO）。
///     <list type="bullet">
///       <item>物品：<c>GET /battlepass/api/admin/query/items?q=&amp;source=&amp;limit=</c> → <c>{ success, items:[{tpl,name,shortName,parent,isMod,canSellOnRagfair}] }</c></item>
///       <item>任务：<c>GET /battlepass/api/admin/query/tasks?q=&amp;limit=</c> → <c>{ success, tasks:[{id,title,scope,conditionType,count,description}] }</c></item>
///     </list>
///     图标统一走 <c>/battlepass/api/icons/{tpl}</c>。前端共用 <c>picker.js</c> 渲染。
/// </summary>
[Injectable]
[ApiController]
[Route("battlepass/api/admin/query")]
public class BattlePassQueryController(ItemSearchService itemSearch)
{
    private static bool Auth(string? token) => WebRegisterController.IsAdminAuthorized(token);

    /// <summary>检索物品（按名称/简称/tpl）。source = all | mod | vanilla。</summary>
    [HttpGet("items")]
    public object Items(
        [FromQuery] string? q,
        [FromQuery] string? source,
        [FromQuery] int? limit,
        [FromHeader(Name = "X-Admin-Token")] string? token = null
    )
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var capped = limit is > 0 and <= 100 ? limit.Value : 30;
        return new { success = true, items = itemSearch.Search(q, source, capped) };
    }

    /// <summary>检索任务模板（按 id / 标题，忽略大小写）。供奖励轨/其他模块引用任务时图形化选择。</summary>
    [HttpGet("tasks")]
    public object Tasks(
        [FromQuery] string? q,
        [FromQuery] int? limit,
        [FromHeader(Name = "X-Admin-Token")] string? token = null
    )
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var capped = limit is > 0 and <= 100 ? limit.Value : 30;
        var query = (q ?? "").Trim();
        var queryLower = query.ToLowerInvariant();

        IEnumerable<BpTaskTemplate> source = BattlePassStore.GetTasks();
        if (query.Length > 0)
        {
            source = source.Where(t =>
                (t.Id?.ToLowerInvariant().Contains(queryLower) ?? false)
                || (t.Title?.ToLowerInvariant().Contains(queryLower) ?? false)
            );
        }

        var tasks = source
            .Take(capped)
            .Select(t => new
            {
                id = t.Id,
                title = t.Title,
                scope = t.Scope,
                conditionType = t.ConditionType,
                count = t.Count,
                description = t.Description,
            })
            .ToList();

        return new { success = true, tasks };
    }
}
