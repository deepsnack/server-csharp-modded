using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using SPTarkov.DI.Annotations;

namespace SPTarkov.Server.Core.BattlePass.Controllers;

/// <summary>
///     通行证称号「对外公开只读」接口（无鉴权）——供 Fika 等其他 mod 查询任意玩家当前佩戴的称号并渲染。
///     <list type="bullet">
///       <item><c>GET /battlepass/api/title/{profileId}</c>：单个玩家当前佩戴称号。</item>
///       <item><c>POST /battlepass/api/titles</c>（body <c>{profileIds:[...]}</c>）：批量（raid 昵称牌）。</item>
///       <item><c>POST /battlepass/api/titles-by-nickname</c>（body <c>{nicknames:[...]}</c>）：按昵称批量（主菜单在线玩家列表）。</item>
///       <item><c>GET /battlepass/api/title-image/{titleId}</c>：图片称号的 PNG（约定 128×32 透明底）。</item>
///     </list>
///     字面量路由优先于页面 catch-all（同 <c>BattlePassIconController</c>），不会被遮蔽。
/// </summary>
[Injectable]
[ApiController]
public class BattlePassTitleController(BattlePassService battlePassService)
{
    private const int MaxBatch = 100;

    // 称号 id 安全 slug：杜绝路径穿越/注入（也是图片文件名约束）。
    private static readonly Regex SafeTitleId = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

    [HttpGet("battlepass/api/title/{profileId}")]
    public object GetTitle(string profileId)
    {
        if (!IsProfileId(profileId))
        {
            return new { success = false, message = "profileId 非法" };
        }

        return new { success = true, title = BattlePassTitleApi.GetEquippedTitle(profileId) };
    }

    [HttpPost("battlepass/api/titles")]
    public object GetTitles([FromBody] JsonElement request)
    {
        var ids = new List<string>();
        if (request.ValueKind == JsonValueKind.Object
            && request.TryGetProperty("profileIds", out var arr)
            && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray())
            {
                var pid = e.GetString();
                if (!string.IsNullOrWhiteSpace(pid) && IsProfileId(pid))
                {
                    ids.Add(pid);
                }

                if (ids.Count >= MaxBatch)
                {
                    break;
                }
            }
        }

        return new { success = true, titles = BattlePassTitleApi.GetEquippedTitles(ids) };
    }

    [HttpPost("battlepass/api/titles-by-nickname")]
    public object GetTitlesByNickname([FromBody] JsonElement request)
    {
        var names = new List<string>();
        if (request.ValueKind == JsonValueKind.Object
            && request.TryGetProperty("nicknames", out var arr)
            && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray())
            {
                var n = e.GetString();
                if (!string.IsNullOrWhiteSpace(n))
                {
                    names.Add(n);
                }

                if (names.Count >= MaxBatch)
                {
                    break;
                }
            }
        }

        return new { success = true, titles = battlePassService.GetEquippedTitlesByNickname(names) };
    }

    [HttpGet("battlepass/api/title-image/{titleId}")]
    public IActionResult GetTitleImage(string titleId)
    {
        if (string.IsNullOrEmpty(titleId) || !SafeTitleId.IsMatch(titleId))
        {
            return new NotFoundResult();
        }

        var path = BattlePassStore.TitleImagePath(titleId);
        if (!File.Exists(path))
        {
            return new NotFoundResult();
        }

        return new FileContentResult(File.ReadAllBytes(path), "image/png");
    }

    private static bool IsProfileId(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length != 24)
        {
            return false;
        }

        foreach (var c in s)
        {
            var hex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!hex)
            {
                return false;
            }
        }

        return true;
    }
}
