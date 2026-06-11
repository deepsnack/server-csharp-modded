using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SPTarkov.Server.Core.BattlePass.ItemControl;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.DI.Annotations;

namespace SPTarkov.Server.Core.BattlePass.Controllers;

/// <summary>
///     物品管控管理端 API（X-Admin-Token，复用 WebRegister admin 域）：
///     物品搜索、获取图谱、增删获取途径（写 override 并即时 Sync）。
/// </summary>
[Injectable]
[ApiController]
[Route("battlepass/api/admin/items")]
public class BattlePassItemAdminController(
    ItemSearchService searchService,
    ItemGraphService graphService,
    ItemControlSync controlSync
)
{
    private static bool Auth(string? token) => WebRegisterController.IsAdminAuthorized(token);

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

    [HttpGet("acquisitions/{tpl}")]
    public object Acquisitions(string tpl, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var graph = graphService.GetAcquisitions(tpl);
        return graph is null ? new { success = false, message = "物品不存在" } : new { success = true, graph };
    }

    /// <summary>新增或移除一条获取途径（body = BpItemOverride）。写 override（按 id 去重）并即时重放。</summary>
    [HttpPost("edit")]
    public object Edit([FromBody] BpItemOverride ov, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        if (string.IsNullOrWhiteSpace(ov.Tpl) || string.IsNullOrWhiteSpace(ov.Source) || string.IsNullOrWhiteSpace(ov.Op))
        {
            return new { success = false, message = "缺少 tpl / source / op" };
        }

        if (ov.Source == AcqSource.StartInv && ov.Op == "add")
        {
            return new { success = false, message = "初始库存暂不支持新增（涉及背包网格摆放）" };
        }

        // 生成稳定 id 用于去重（同一编辑重复提交不累积）
        if (string.IsNullOrWhiteSpace(ov.Id))
        {
            ov.Id = $"{ov.Source}:{ov.Op}:{ov.Tpl}:{ov.TraderId}:{ov.QuestId}:{ov.RewardGroup}:{ov.RecipeId}:{ov.Role}:{ov.LocationId}:{ov.ContainerOrSpawn}:{ov.Side}";
        }

        var list = BattlePassStore.GetItemOverrides();
        list.RemoveAll(o => o.Id == ov.Id);
        list.Add(ov);
        BattlePassStore.SaveItemOverrides(list);
        controlSync.Sync();
        return new { success = true };
    }

    [HttpGet("overrides")]
    public object GetOverrides([FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        return new { success = true, overrides = BattlePassStore.GetItemOverrides() };
    }

    /// <summary>删除一条 override（撤销）。结构类「移除」撤销在下次重启恢复；loot 撤销即时生效。</summary>
    [HttpDelete("overrides")]
    public object DeleteOverride([FromBody] JsonElement request, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var id = request.TryGetProperty("id", out var i) ? i.GetString() : null;
        if (string.IsNullOrEmpty(id))
        {
            return new { success = false, message = "缺少 id" };
        }

        var list = BattlePassStore.GetItemOverrides();
        var removed = list.RemoveAll(o => o.Id == id);
        BattlePassStore.SaveItemOverrides(list);
        controlSync.Sync();
        return new { success = removed > 0 };
    }
}
