using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace SPTarkov.Server.Core.BattlePass.Controllers;

/// <summary>
///     通行证玩家端 API。登录后所有请求带 X-BP-Token（→ profileId）。
///     返回匿名对象，由 MVC 序列化（与 WebRegisterController 一致）。
/// </summary>
[Injectable]
[ApiController]
[Route("battlepass/api")]
public class BattlePassController(
    BattlePassService battlePassService,
    BattlePassTrackService trackService,
    ActivationCodeService activationCodeService,
    ISptLogger<BattlePassController> logger
)
{
    private const int FreeDailyRerolls = 1;

    /// <summary>从 SPT 客户端请求的 PHPSESSID cookie 解析会话 id（= profileId）。客户端插件经 RequestHandler 自动携带。</summary>
    private static string? ResolveSptSession(string? cookieHeader)
    {
        if (string.IsNullOrEmpty(cookieHeader))
        {
            return null;
        }

        foreach (var part in cookieHeader.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals("PHPSESSID", StringComparison.OrdinalIgnoreCase))
            {
                var v = kv[1].Trim();
                return string.IsNullOrEmpty(v) ? null : v;
            }
        }

        return null;
    }

    [HttpPost("login")]
    public object Login([FromBody] JsonElement request)
    {
        try
        {
            var username = request.TryGetProperty("username", out var u) ? u.GetString() : null;
            var password = request.TryGetProperty("password", out var p) ? p.GetString() : null;

            var profileId = battlePassService.VerifyLogin(username, password);
            if (profileId is null)
            {
                return new { success = false, message = "用户名或密码错误（或该账号尚未设置密码）" };
            }

            var token = BattlePassSession.Issue(profileId);
            return new { success = true, token, username };
        }
        catch (Exception ex)
        {
            return new { success = false, message = ex.Message };
        }
    }

    [HttpGet("state")]
    public object GetState([FromHeader(Name = "X-BP-Token")] string? token = null)
    {
        var profileId = BattlePassSession.Resolve(token);
        if (profileId is null)
        {
            return new { success = false, message = "未登录或会话已过期" };
        }

        try
        {
            var season = BattlePassStore.GetSeason();
            var prog = battlePassService.GetOrResetProgress(profileId, season);

            // 进度由客户端 /track 上报结算；此处只按周期滚动刷新活跃任务
            if (trackService.RefreshActiveTasks(profileId, prog))
            {
                BattlePassStore.SaveProgress(profileId, prog);
            }

            var tracks = BattlePassStore.GetTracks();
            var levels = tracks
                .OrderBy(kv => kv.Key)
                .Select(kv => new
                {
                    level = kv.Key,
                    free = kv.Value.Free,
                    premium = kv.Value.Premium,
                    claimedFree = prog.ClaimedFree.Contains(kv.Key),
                    claimedPremium = prog.ClaimedPremium.Contains(kv.Key),
                    unlocked = kv.Key <= prog.Level,
                })
                .ToList();

            // 满级循环奖励配置（tracks 保留键 0）
            tracks.TryGetValue(BattlePassStore.CycleRewardLevelKey, out var cycleRewards);
            var atMaxLevel = prog.Level >= season.MaxLevel;

            return new
            {
                success = true,
                season = new
                {
                    season.SeasonId,
                    season.Name,
                    season.StartUtc,
                    season.EndUtc,
                    season.MaxLevel,
                    cycleXp = season.CycleXpEffective,
                },
                progress = new
                {
                    prog.Level,
                    prog.Xp,
                    xpToNext = atMaxLevel ? season.CycleXpEffective : season.XpToReach(prog.Level + 1),
                    prog.PremiumUnlocked,
                    prog.CyclesCompleted,
                    atMaxLevel,
                    username = battlePassService.GetUsername(profileId),
                },
                levels,
                cycle = new
                {
                    free = cycleRewards?.Free ?? new List<BpReward>(),
                    premium = cycleRewards?.Premium ?? new List<BpReward>(),
                    completed = prog.CyclesCompleted,
                    claimedFree = prog.ClaimedCycleFree,
                    claimedPremium = prog.ClaimedCyclePremium,
                },
            };
        }
        catch (Exception ex)
        {
            logger.Error($"[SPT-BattlePass] state 失败: {ex.Message}");
            return new { success = false, message = ex.Message };
        }
    }

    [HttpGet("tasks")]
    public object GetTasks([FromHeader(Name = "X-BP-Token")] string? token = null)
    {
        var profileId = BattlePassSession.Resolve(token);
        if (profileId is null)
        {
            return new { success = false, message = "未登录或会话已过期" };
        }

        try
        {
            var season = BattlePassStore.GetSeason();
            var prog = battlePassService.GetOrResetProgress(profileId, season);

            // 进度来自客户端 /track 上报；任务页只按周期滚动刷新活跃任务
            if (trackService.RefreshActiveTasks(profileId, prog))
            {
                BattlePassStore.SaveProgress(profileId, prog);
            }

            var templates = BattlePassStore.GetTasks().ToDictionary(t => t.Id);
            var tasks = prog.ActiveTasks
                .Select(a =>
                {
                    templates.TryGetValue(a.TaskId, out var tpl);
                    return new
                    {
                        a.TaskId,
                        a.Scope,
                        title = tpl?.Title ?? a.TaskId,
                        description = tpl?.Description ?? "",
                        xp = tpl?.Xp ?? 0,
                        target = tpl?.Count ?? 0,
                        progress = a.Progress,
                        completed = a.CreditedXp,
                        rotation = tpl?.Rotation ?? "fixed",
                    };
                })
                .ToList();

            return new
            {
                success = true,
                tasks,
                freeRerollsLeft = Math.Max(0, FreeDailyRerolls - prog.DailyRerollsUsed),
            };
        }
        catch (Exception ex)
        {
            return new { success = false, message = ex.Message };
        }
    }

    [HttpPost("tasks/refresh")]
    public object RefreshTasks([FromBody] JsonElement request, [FromHeader(Name = "X-BP-Token")] string? token = null)
    {
        var profileId = BattlePassSession.Resolve(token);
        if (profileId is null)
        {
            return new { success = false, message = "未登录或会话已过期" };
        }

        try
        {
            var scope = request.TryGetProperty("scope", out var s) ? s.GetString() ?? "daily" : "daily";
            if (!string.Equals(scope, "daily", StringComparison.OrdinalIgnoreCase))
            {
                return new { success = false, message = "玩家端仅支持刷新每日任务" };
            }

            var season = BattlePassStore.GetSeason();
            var prog = battlePassService.GetOrResetProgress(profileId, season);
            trackService.RefreshActiveTasks(profileId, prog);

            if (prog.DailyRerollsUsed >= FreeDailyRerolls)
            {
                BattlePassStore.SaveProgress(profileId, prog);
                return new { success = false, message = "今日免费刷新次数已用完" };
            }

            if (!trackService.ForceRefreshTasks(profileId, prog, "daily"))
            {
                BattlePassStore.SaveProgress(profileId, prog);
                return new { success = false, message = "无可刷新的每日任务" };
            }

            prog.DailyRerollsUsed++;
            BattlePassStore.SaveProgress(profileId, prog);
            return new { success = true, freeRerollsLeft = Math.Max(0, FreeDailyRerolls - prog.DailyRerollsUsed) };
        }
        catch (Exception ex)
        {
            return new { success = false, message = ex.Message };
        }
    }

    [HttpPost("tasks/reroll")]
    public object Reroll([FromBody] JsonElement request, [FromHeader(Name = "X-BP-Token")] string? token = null)
    {
        var profileId = BattlePassSession.Resolve(token);
        if (profileId is null)
        {
            return new { success = false, message = "未登录或会话已过期" };
        }

        try
        {
            var taskId = request.TryGetProperty("taskId", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(taskId))
            {
                return new { success = false, message = "缺少 taskId" };
            }

            var season = BattlePassStore.GetSeason();
            var prog = battlePassService.GetOrResetProgress(profileId, season);
            trackService.RefreshActiveTasks(profileId, prog);

            var active = prog.ActiveTasks.FirstOrDefault(t => string.Equals(t.TaskId, taskId, StringComparison.OrdinalIgnoreCase));
            if (active is null)
            {
                BattlePassStore.SaveProgress(profileId, prog);
                return new { success = false, message = "任务不存在或已过期" };
            }

            if (!string.Equals(active.Scope, "daily", StringComparison.OrdinalIgnoreCase) || active.CreditedXp)
            {
                BattlePassStore.SaveProgress(profileId, prog);
                return new { success = false, message = "该任务不可刷新" };
            }

            if (prog.DailyRerollsUsed >= FreeDailyRerolls)
            {
                BattlePassStore.SaveProgress(profileId, prog);
                return new { success = false, message = "今日免费刷新次数已用完" };
            }

            if (!trackService.RerollTask(profileId, prog, taskId))
            {
                return new { success = false, message = "该任务不可刷新（或无可替换任务）" };
            }

            prog.DailyRerollsUsed++;
            BattlePassStore.SaveProgress(profileId, prog);
            return new { success = true };
        }
        catch (Exception ex)
        {
            return new { success = false, message = ex.Message };
        }
    }

    // ============================ 客户端插件接口（按 SPT 会话鉴权，非网页 token） ============================

    /// <summary>
    ///     客户端插件拉取当前活跃任务（含条件与进度）。会话来自 SPT 的 PHPSESSID cookie（= profileId），
    ///     RequestHandler 自动携带。纯读 + 周期滚动刷新，不触碰游戏档案。
    /// </summary>
    [HttpGet("active-tasks")]
    public object GetActiveTasks([FromHeader(Name = "Cookie")] string? cookie = null)
    {
        var profileId = ResolveSptSession(cookie);
        if (profileId is null)
        {
            return new { success = false, message = "无有效会话" };
        }

        try
        {
            var season = BattlePassStore.GetSeason();
            var prog = battlePassService.GetOrResetProgress(profileId, season);
            if (trackService.RefreshActiveTasks(profileId, prog))
            {
                BattlePassStore.SaveProgress(profileId, prog);
            }

            var templates = BattlePassStore.GetTasks().ToDictionary(t => t.Id);
            var tasks = prog.ActiveTasks
                .Select(a =>
                {
                    templates.TryGetValue(a.TaskId, out var tpl);
                    return new
                    {
                        a.TaskId,
                        a.Scope,
                        conditionType = tpl?.ConditionType ?? "Kills",
                        target = tpl?.Target ?? "Any",
                        count = tpl?.Count ?? 0,
                        location = tpl?.Location,
                        zoneId = tpl?.ZoneId,
                        itemRequirements = tpl?.ItemRequirements ?? new List<BpTaskItemRequirement>(),
                        singleRaid = tpl?.SingleRaid ?? false,
                        progress = a.Progress,
                        done = a.CreditedXp,
                    };
                })
                .ToList();

            return new { success = true, tasks };
        }
        catch (Exception ex)
        {
            return new { success = false, message = ex.Message };
        }
    }

    /// <summary>
    ///     客户端插件实时上报战绩（逐事件累计快照）；服务端按任务条件增量累计/结算。
    ///     <para><b>手动读 body</b>：SPT 客户端 <c>RequestHandler</c> 发来的是 <b>zlib 压缩 + octet-stream</b>，
    ///     MVC 的 <c>[FromBody]</c> 会因 Content-Type 非 application/json 直接回 415（曾导致进度永远不入账）；
    ///     故这里直接读 <see cref="HttpRequest.Body"/>，自适应 zlib 解压后手动反序列化，
    ///     同时兼容浏览器直发的未压缩 JSON（便于调试）。会话来自 PHPSESSID cookie（= profileId）。</para>
    /// </summary>
    [HttpPost("track")]
    public async Task<object> Track(HttpRequest request, [FromHeader(Name = "Cookie")] string? cookie = null)
    {
        var profileId = ResolveSptSession(cookie);
        if (profileId is null)
        {
            return new { success = false, message = "无有效会话" };
        }

        try
        {
            var payload = await ReadTrackPayloadAsync(request);
            if (payload is null)
            {
                return new { success = false, message = "空载荷或解析失败" };
            }

            var season = BattlePassStore.GetSeason();
            var prog = battlePassService.GetOrResetProgress(profileId, season);
            trackService.RefreshActiveTasks(profileId, prog); // 确保活跃任务为当期
            var result = trackService.ApplyRaidTrack(profileId, prog, season, payload);
            BattlePassStore.SaveProgress(profileId, prog);

            return new { success = true, duplicate = result.Duplicate, credited = result.Credited };
        }
        catch (Exception ex)
        {
            logger.Error($"[SPT-BattlePass] track 失败: {ex.Message}");
            return new { success = false, message = ex.Message };
        }
    }

    /// <summary>读取并反序列化战绩载荷：自适应 zlib（SPT RequestHandler）/ 明文 JSON（浏览器）。</summary>
    private static async Task<RaidTrackPayload?> ReadTrackPayloadAsync(HttpRequest request)
    {
        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms);
        var raw = ms.ToArray();
        if (raw.Length == 0)
        {
            return null;
        }

        var json = raw;
        // zlib 魔数：首字节 0x78，次字节 0x01/0x9C/0xDA（SPT 用 ZlibCompression.Maximum → 0x78 0xDA）
        if (raw.Length >= 2 && raw[0] == 0x78 && (raw[1] == 0x01 || raw[1] == 0x9C || raw[1] == 0xDA))
        {
            using var input = new MemoryStream(raw);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            await zlib.CopyToAsync(outMs);
            json = outMs.ToArray();
        }

        return JsonSerializer.Deserialize<RaidTrackPayload>(json);
    }

    [HttpPost("claim")]
    public object Claim([FromBody] JsonElement request, [FromHeader(Name = "X-BP-Token")] string? token = null)
    {
        var profileId = BattlePassSession.Resolve(token);
        if (profileId is null)
        {
            return new { success = false, message = "未登录或会话已过期" };
        }

        try
        {
            var level = request.TryGetProperty("level", out var l) ? l.GetInt32() : 0;
            var track = request.TryGetProperty("track", out var tr) ? tr.GetString() ?? "free" : "free";

            var season = BattlePassStore.GetSeason();
            var prog = battlePassService.GetOrResetProgress(profileId, season);
            var (ok, message) = battlePassService.Claim(profileId, prog, season, level, track);
            return new { success = ok, message };
        }
        catch (Exception ex)
        {
            return new { success = false, message = ex.Message };
        }
    }

    /// <summary>领取满级后某一轮循环奖励（body { cycle, track }）。</summary>
    [HttpPost("claim-cycle")]
    public object ClaimCycle([FromBody] JsonElement request, [FromHeader(Name = "X-BP-Token")] string? token = null)
    {
        var profileId = BattlePassSession.Resolve(token);
        if (profileId is null)
        {
            return new { success = false, message = "未登录或会话已过期" };
        }

        try
        {
            var cycle = request.TryGetProperty("cycle", out var c) ? c.GetInt32() : 0;
            var track = request.TryGetProperty("track", out var tr) ? tr.GetString() ?? "free" : "free";

            var season = BattlePassStore.GetSeason();
            var prog = battlePassService.GetOrResetProgress(profileId, season);
            var (ok, message) = battlePassService.ClaimCycle(profileId, prog, season, cycle, track);
            return new { success = ok, message };
        }
        catch (Exception ex)
        {
            return new { success = false, message = ex.Message };
        }
    }

    /// <summary>当前玩家拥有的称号列表（解析成对外视图，并标注当前佩戴项）。</summary>
    [HttpGet("my-titles")]
    public object GetMyTitles([FromHeader(Name = "X-BP-Token")] string? token = null)
    {
        var profileId = BattlePassSession.Resolve(token);
        if (profileId is null)
        {
            return new { success = false, message = "未登录或会话已过期" };
        }

        try
        {
            var player = BattlePassStore.GetPlayerTitles(profileId);
            var catalog = BattlePassStore.GetTitleCatalog().ToDictionary(t => t.Id);
            var owned = player.Owned
                .Where(catalog.ContainsKey)
                .Select(id => BattlePassTitleApi.ToView(catalog[id]))
                .Where(v => v is not null)
                .ToList();

            return new { success = true, equipped = player.Equipped, titles = owned };
        }
        catch (Exception ex)
        {
            return new { success = false, message = ex.Message };
        }
    }

    /// <summary>佩戴/卸下称号（body { titleId }，空=卸下；须已拥有）。</summary>
    [HttpPost("my-titles/equip")]
    public object EquipTitle([FromBody] JsonElement request, [FromHeader(Name = "X-BP-Token")] string? token = null)
    {
        var profileId = BattlePassSession.Resolve(token);
        if (profileId is null)
        {
            return new { success = false, message = "未登录或会话已过期" };
        }

        try
        {
            var titleId = request.TryGetProperty("titleId", out var t) ? t.GetString() : null;
            var ok = BattlePassStore.EquipTitle(profileId, titleId);
            return new { success = ok, message = ok ? "" : "你尚未拥有该称号" };
        }
        catch (Exception ex)
        {
            return new { success = false, message = ex.Message };
        }
    }

    [HttpPost("redeem")]
    public object Redeem([FromBody] JsonElement request, [FromHeader(Name = "X-BP-Token")] string? token = null)
    {
        var profileId = BattlePassSession.Resolve(token);
        if (profileId is null)
        {
            return new { success = false, message = "未登录或会话已过期" };
        }

        try
        {
            var code = request.TryGetProperty("code", out var c) ? c.GetString() : null;
            var season = BattlePassStore.GetSeason();
            var prog = battlePassService.GetOrResetProgress(profileId, season);

            var (ok, message) = activationCodeService.Redeem(profileId, prog, season, code);
            if (ok)
            {
                BattlePassStore.SaveProgress(profileId, prog);
            }

            return new { success = ok, message };
        }
        catch (Exception ex)
        {
            return new { success = false, message = ex.Message };
        }
    }
}
