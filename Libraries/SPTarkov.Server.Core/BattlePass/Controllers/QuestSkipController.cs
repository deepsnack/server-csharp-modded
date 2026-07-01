using Microsoft.AspNetCore.Mvc;
using SPTarkov.DI.Annotations;

namespace SPTarkov.Server.Core.BattlePass.Controllers;

/// <summary>通行证任务目标跳过玩家 API。</summary>
[Injectable]
[ApiController]
[Route("battlepass/api/quest-skip")]
public class QuestSkipController(QuestSkipService questSkipService) : ControllerBase
{
    [HttpGet("state")]
    public QuestSkipStateResult State([FromHeader(Name = "X-BP-Token")] string? token = null)
    {
        var profileId = BattlePassSession.Resolve(token);
        return profileId is null
            ? new QuestSkipStateResult { Success = false, Message = "未登录或会话已过期" }
            : questSkipService.GetState(profileId);
    }

    [HttpPost("objectives/skip")]
    public async Task<QuestSkipResult> Skip(
        [FromBody] QuestSkipRequest request,
        [FromHeader(Name = "X-BP-Token")] string? token = null
    )
    {
        var profileId = BattlePassSession.Resolve(token);
        return profileId is null
            ? new QuestSkipResult { Success = false, Message = "未登录或会话已过期" }
            : await questSkipService.SkipAsync(profileId, request.ActionId);
    }
}
