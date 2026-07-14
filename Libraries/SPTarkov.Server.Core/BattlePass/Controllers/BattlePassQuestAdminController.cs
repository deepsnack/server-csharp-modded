using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SPTarkov.Server.Core.BattlePass.Administration;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.DI.Annotations;

namespace SPTarkov.Server.Core.BattlePass.Controllers;

/// <summary>
///     商人任务管理端 API：依赖图谱与详情读取、原版任务禁用/奖励覆盖、自定义任务增删改。
///     <para>读端点走 <see cref="QuestGraphService"/>；写端点复用 <see cref="QuestChangeHandler"/>
///     的 Normalize/Validate/ApplyAndActivate，正常管理员即时落盘 + <see cref="QuestSync.Sync"/> 热重放，
///     协管走 /review/submit 审核后同一处理器放行，二者业务逻辑一致。</para>
///     鉴权统一走 <see cref="BattlePassAdminSessionService.ValidateToken"/>（兼容原始 token 与会话 token）。
/// </summary>
[Injectable]
[ApiController]
[Route("battlepass/api/admin/quests")]
public class BattlePassQuestAdminController(
    QuestGraphService graphService,
    QuestChangeHandler questChangeHandler,
    BattlePassAdminSessionService sessionService,
    ISptLogger<BattlePassQuestAdminController> logger
)
{
    private bool Auth(string? token) => sessionService.ValidateToken(token)?.IsAdmin == true;
    private bool CanRead(string? token, string capability) => sessionService.ValidateToken(token)?.HasCapability(capability) == true;

    // ============================ 读端点 ============================

    /// <summary>商人清单（含名称，用于任务归属筛选）。</summary>
    [HttpGet("traders")]
    public object GetTraders([FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!CanRead(token, "quests.read"))
        {
            return new { success = false, message = "未授权" };
        }

        return new { success = true, traders = graphService.GetTraders() };
    }

    /// <summary>自定义条件目录：地图 target 与可选择的击杀阵营/bot role。</summary>
    [HttpGet("catalog")]
    public object GetCatalog([FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!CanRead(token, "quests.read"))
        {
            return new { success = false, message = "未授权" };
        }

        return new
        {
            success = true,
            locations = graphService.GetLocations(),
            killTargets = graphService.GetKillTargets(),
        };
    }

    /// <summary>检索指定商人的真实货架根商品，供 AssortmentUnlock 奖励选择。</summary>
    [HttpGet("assorts")]
    public object GetAssorts(
        [FromQuery] string? traderId,
        [FromQuery] string? q,
        [FromQuery] int? limit,
        [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!CanRead(token, "quests.read"))
        {
            return new { success = false, message = "未授权" };
        }

        return new
        {
            success = true,
            assorts = graphService.SearchAssorts(traderId, q, limit is > 0 and <= 100 ? limit.Value : 30),
        };
    }

    /// <summary>任务清单（可按商人过滤、按名称/id 关键字搜索）。</summary>
    [HttpGet("list")]
    public object ListQuests(
        [FromQuery] string? traderId,
        [FromQuery] string? q,
        [FromHeader(Name = "X-Admin-Token")] string? token = null
    )
    {
        if (!CanRead(token, "quests.read"))
        {
            return new { success = false, message = "未授权" };
        }

        return new { success = true, quests = graphService.ListQuests(traderId, q) };
    }

    /// <summary>某商人的任务依赖图谱（解锁链 + 互斥/失败边）。traderId 为空则返回全部。</summary>
    [HttpGet("graph")]
    public object GetGraph([FromQuery] string? traderId, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!CanRead(token, "quests.read"))
        {
            return new { success = false, message = "未授权" };
        }

        return new { success = true, graph = graphService.GetGraph(traderId) };
    }

    /// <summary>单个任务详情：前置条件、完成目标、各阶段奖励（本地化解析为中文）。</summary>
    [HttpGet("detail/{questId}")]
    public object GetDetail(string questId, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!CanRead(token, "quests.read"))
        {
            return new { success = false, message = "未授权" };
        }

        var detail = graphService.GetQuestDetail(questId);
        return detail is null ? new { success = false, message = "任务不存在" } : new { success = true, detail };
    }

    /// <summary>当前所有原版任务覆盖（禁用/奖励替换）。</summary>
    [HttpGet("overrides")]
    public object GetOverrides([FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!CanRead(token, "quests.read"))
        {
            return new { success = false, message = "未授权" };
        }

        return new { success = true, overrides = BattlePassStore.GetQuestOverrides() };
    }

    /// <summary>当前所有自定义任务。</summary>
    [HttpGet("custom")]
    public object GetCustom([FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!CanRead(token, "quests.read"))
        {
            return new { success = false, message = "未授权" };
        }

        return new { success = true, quests = BattlePassStore.GetCustomQuests() };
    }

    // ============================ 写端点（管理员即时生效） ============================

    /// <summary>原版任务覆盖：禁用/启用 + 奖励桶替换（body = BpQuestOverride 的 JSON）。</summary>
    [HttpPost("override")]
    public object Override([FromBody] JsonElement body, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        return DispatchWrite("quest.override", body, token);
    }

    /// <summary>新建或编辑自定义商人任务（body = BpCustomQuest 的 JSON；无 id 视为新建）。</summary>
    [HttpPost("custom")]
    public object CustomUpsert([FromBody] JsonElement body, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        return DispatchWrite("quest.customUpsert", body, token);
    }

    /// <summary>删除自定义任务（body: {id}）。删除后即时重放，注入项从内存 DB 移除，无需重启。</summary>
    [HttpDelete("custom")]
    public object CustomDelete([FromBody] JsonElement body, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        return DispatchWrite("quest.customDelete", body, token);
    }

    /// <summary>统一写入分发：规范化 → 校验 → 即时落盘并热重放（管理员越过审核直接生效）。</summary>
    private object DispatchWrite(string commandType, JsonElement body, string? token)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        try
        {
            var normalized = questChangeHandler.Normalize(commandType, body);
            var err = questChangeHandler.Validate(commandType, normalized);
            if (err is not null)
            {
                return new { success = false, message = err };
            }

            // 管理员直写：不做乐观并发校验（expectedBaseRevision = null）
            var revision = questChangeHandler.ApplyAndActivate(commandType, normalized, null, null);
            var targetKey = questChangeHandler.GetTargetKey(commandType, normalized);
            return new { success = true, targetKey, revision };
        }
        catch (Exception ex)
        {
            logger.Error($"[商人任务] {commandType} 写入失败: {ex.Message}");
            return new { success = false, message = ex.Message };
        }
    }
}
