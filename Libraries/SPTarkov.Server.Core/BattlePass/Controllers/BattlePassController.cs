using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services.Portal;

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
    Services.DatabaseService databaseService,
    ItemControl.ItemSearchService itemSearchService,
    BattlePassHandoverService handoverService,
    BattlePassShopService shopService,
    ISptLogger<BattlePassController> logger
) : ControllerBase
{
    private const string PlayerSsoAudience = "battlepass-player";
    private static readonly JtiReplayGuard PlayerSsoReplayGuard = new();
    /// <summary>
    ///     mod 物品兜底：item 类奖励的 tpl 不在物品库（mod 被删除）时从玩家页下发数据中剔除——
    ///     该格自然回退为空（全部剔除时空格）。只过滤下发，存储配置不动：mod 装回后奖励自动恢复。
    ///     非 item 类型（购买权/配方/称号/服装）不在此过滤，其引用失效由各自领取链路兜底。
    ///     展示名兜底：item 类奖励未填展示名时，按本地化解析物品名（原版/mod 通用）下发，
    ///     解析不到才让前端回退到 MongoID——即 展示名 → 物品名 → MongoID。用 record with 生成
    ///     下发副本，不改存储配置。
    /// </summary>
    private List<BpReward> SanitizeRewards(List<BpReward>? rewards)
    {
        if (rewards is null || rewards.Count == 0)
        {
            return [];
        }

        var items = databaseService.GetItems();
        return rewards
            .Where(r =>
            {
                var type = (r.Type ?? "item").Trim().ToLowerInvariant();
                if (type is "purchaseright" or "recipe" or "title" or "clothing" or "lotteryglobaltickets" or "lotterypooltickets" or "lotteryexchangecoins")
                {
                    return true;
                }

                var tpl = r.Tpl?.Trim() ?? "";
                return Models.Common.MongoId.IsValidMongoId(tpl) && items.ContainsKey(new Models.Common.MongoId(tpl));
            })
            .Select(r =>
            {
                var type = (r.Type ?? "item").Trim().ToLowerInvariant();
                var tpl = r.Tpl?.Trim() ?? "";
                if (type == "item" && Models.Common.MongoId.IsValidMongoId(tpl))
                {
                    // 展示名 → 物品名 → MongoID：展示名为空、等于 tpl、或本身就是一个 MongoID
                    //（旧数据把 id 当名字存）时，一律按本地化解析物品名；解析不到则清空 Name，
                    // 交前端回退到 tpl（即 MongoID）。每级回退保证健壮性。
                    var displayName = r.Name?.Trim();
                    var nameLooksLikeId = string.IsNullOrWhiteSpace(displayName)
                        || string.Equals(displayName, tpl, StringComparison.OrdinalIgnoreCase)
                        || Models.Common.MongoId.IsValidMongoId(displayName);
                    if (nameLooksLikeId)
                    {
                        var resolved = itemSearchService.ResolveItemName(new Models.Common.MongoId(tpl));
                        return r with { Name = string.IsNullOrWhiteSpace(resolved) ? null : resolved };
                    }
                }

                return r;
            })
            .ToList();
    }

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

    /// <summary>官网快照复用的公开赛季摘要，不包含任何玩家进度或管理配置。</summary>
    [HttpGet("public/summary")]
    public object GetPublicSummary()
    {
        var season = BattlePassStore.GetSeason();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new
        {
            success = true,
            season.SeasonId,
            season.Name,
            season.StartUtc,
            season.EndUtc,
            active = now >= season.StartUtc && now < season.EndUtc,
            remainingSeconds = Math.Max(0, season.EndUtc - now),
        };
    }

    /// <summary>消费 SptManagerPortal 的玩家 SSO 短令牌，换取通行证自己的玩家会话。</summary>
    [HttpGet("portal-sso")]
    public IActionResult PlayerPortalSso([FromQuery] string? token = null)
    {
        var secret = PortalSharedKey.TryGetPlayerTokenSecret();
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(token)) return Unauthorized();
        var payload = PlayerPortalSsoToken.Verify(token, secret, PlayerSsoAudience);
        if (payload is null || !PlayerSsoReplayGuard.TryAccept(payload.TokenId)) return Unauthorized();
        if (battlePassService.GetUsername(payload.Subject) is null || battlePassService.IsHeadlessProfile(payload.Subject))
            return Unauthorized();
        var battlePassToken = BattlePassSession.Issue(payload.Subject);
        return Redirect("/battlepass#sso=" + Uri.EscapeDataString(battlePassToken));
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
            var pendingCompensation = battlePassService.GetPendingCompensationCount(prog);
            var levels = tracks
                .OrderBy(kv => kv.Key)
                .Select(kv => new
                {
                    level = kv.Key,
                    free = SanitizeRewards(kv.Value.Free),
                    premium = SanitizeRewards(kv.Value.Premium),
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
                compensation = new
                {
                    available = pendingCompensation > 0,
                    pendingCount = pendingCompensation,
                },
                levels,
                cycle = new
                {
                    free = SanitizeRewards(cycleRewards?.Free),
                    premium = SanitizeRewards(cycleRewards?.Premium),
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

            var templates = BattlePassStore.GetAllTasks().ToDictionary(t => t.Id);
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
                        conditionType = tpl?.ConditionType ?? "Kills",
                    };
                })
                .ToList();

            return new
            {
                success = true,
                tasks,
                freeRerollsLeft = Math.Max(0, season.DailyRefreshLimit - prog.DailyRerollsUsed),
                refreshLeft = BuildRefreshLeft(season, prog),
                displayCount = season.TaskDisplayCount,
            };
        }
        catch (Exception ex)
        {
            return new { success = false, message = ex.Message };
        }
    }

    /// <summary>列出某「上交物品」任务当前存档中可上交的物品明细（ID/名称/可上交数量）。</summary>
    [HttpGet("handover/{taskId}")]
    public object GetHandover(string taskId, [FromHeader(Name = "X-BP-Token")] string? token = null)
    {
        var profileId = BattlePassSession.Resolve(token);
        if (profileId is null)
        {
            return new { success = false, message = "未登录或会话已过期" };
        }

        try
        {
            var (ok, message, info) = handoverService.GetEligible(profileId, taskId);
            return ok ? new { success = true, info } : new { success = false, message };
        }
        catch (Exception ex)
        {
            return new { success = false, message = ex.Message };
        }
    }

    /// <summary>对某「上交物品」任务执行网页上交：从存档仓库移除匹配物品并累计任务进度。</summary>
    [HttpPost("handover")]
    public object Handover([FromBody] JsonElement request, [FromHeader(Name = "X-BP-Token")] string? token = null)
    {
        var profileId = BattlePassSession.Resolve(token);
        if (profileId is null)
        {
            return new { success = false, message = "未登录或会话已过期" };
        }

        try
        {
            var taskId = request.TryGetProperty("taskId", out var t) ? t.GetString() : null;
            var (ok, message, result) = handoverService.Handover(profileId, taskId);
            return ok ? new { success = true, result } : new { success = false, message };
        }
        catch (Exception ex)
        {
            return new { success = false, message = ex.Message };
        }
    }

    /// <summary>网页通行证商店货架：管理员自定义条目（含每项是否买得起/库存/限购）。</summary>
    [HttpGet("shop")]
    public object GetShop([FromHeader(Name = "X-BP-Token")] string? token = null)
    {
        var profileId = BattlePassSession.Resolve(token);
        if (profileId is null)
        {
            return new { success = false, message = "未登录或会话已过期" };
        }

        try
        {
            return new { success = true, catalog = shopService.GetCatalog(profileId) };
        }
        catch (Exception ex)
        {
            return new { success = false, message = ex.Message };
        }
    }

    /// <summary>网页商店批量购买：校验数量、限购、库存与货币足额→扣减→邮件发货。</summary>
    [HttpPost("shop/buy")]
    public object BuyShop([FromBody] JsonElement request, [FromHeader(Name = "X-BP-Token")] string? token = null)
    {
        var profileId = BattlePassSession.Resolve(token);
        if (profileId is null)
        {
            return new { success = false, message = "未登录或会话已过期" };
        }

        try
        {
            var offerId = request.TryGetProperty("offerId", out var o) ? o.GetString() : null;
            var quantity = 1;
            if (
                request.TryGetProperty("quantity", out var q)
                && (q.ValueKind != JsonValueKind.Number || !q.TryGetInt32(out quantity))
            )
            {
                return new { success = false, message = "购买数量格式无效" };
            }

            var (ok, message) = shopService.Buy(profileId, offerId ?? "", quantity);
            return new { success = ok, message };
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
            var scope = (request.TryGetProperty("scope", out var s) ? s.GetString() ?? "daily" : "daily")
                .Trim().ToLowerInvariant();
            if (scope is not ("daily" or "weekly" or "season"))
            {
                return new { success = false, message = "无效的刷新范围" };
            }

            var season = BattlePassStore.GetSeason();
            var prog = battlePassService.GetOrResetProgress(profileId, season);
            // 先按周期自动滚动（可能重置对应 scope 的刷新预算），再判定主动刷新额度
            trackService.RefreshActiveTasks(profileId, prog);

            var limit = RefreshLimitForScope(season, scope);
            if (limit <= 0)
            {
                BattlePassStore.SaveProgress(profileId, prog);
                return new { success = false, message = "该类任务不支持主动刷新" };
            }

            if (RefreshUsedForScope(prog, scope) >= limit)
            {
                BattlePassStore.SaveProgress(profileId, prog);
                return new { success = false, message = "刷新次数已用完" };
            }

            if (!trackService.ForceRefreshTasks(profileId, prog, scope))
            {
                BattlePassStore.SaveProgress(profileId, prog);
                return new { success = false, message = "无可刷新的任务" };
            }

            SetRefreshUsedForScope(prog, scope, RefreshUsedForScope(prog, scope) + 1);
            BattlePassStore.SaveProgress(profileId, prog);
            return new { success = true, refreshLeft = BuildRefreshLeft(season, prog) };
        }
        catch (Exception ex)
        {
            return new { success = false, message = ex.Message };
        }
    }

    private static int RefreshLimitForScope(BpSeason season, string scope)
    {
        return scope switch
        {
            "weekly" => season.WeeklyRefreshLimit,
            "season" => season.SeasonRefreshLimit,
            _ => season.DailyRefreshLimit,
        };
    }

    private static int RefreshUsedForScope(BpProgress prog, string scope)
    {
        return scope switch
        {
            "weekly" => prog.WeeklyRefreshUsed,
            "season" => prog.SeasonRefreshUsed,
            _ => prog.DailyRerollsUsed,
        };
    }

    private static void SetRefreshUsedForScope(BpProgress prog, string scope, int value)
    {
        switch (scope)
        {
            case "weekly":
                prog.WeeklyRefreshUsed = value;
                break;
            case "season":
                prog.SeasonRefreshUsed = value;
                break;
            default:
                prog.DailyRerollsUsed = value;
                break;
        }
    }

    /// <summary>三类任务各自剩余的主动刷新次数（供前端展示与按钮禁用）。</summary>
    private static object BuildRefreshLeft(BpSeason season, BpProgress prog)
    {
        return new
        {
            daily = Math.Max(0, season.DailyRefreshLimit - prog.DailyRerollsUsed),
            weekly = Math.Max(0, season.WeeklyRefreshLimit - prog.WeeklyRefreshUsed),
            season = Math.Max(0, season.SeasonRefreshLimit - prog.SeasonRefreshUsed),
        };
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

            if (prog.DailyRerollsUsed >= season.DailyRefreshLimit)
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

            var templates = BattlePassStore.GetAllTasks().ToDictionary(t => t.Id);
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
    public async Task<object> Track([FromHeader(Name = "Cookie")] string? cookie = null)
    {
        var profileId = ResolveSptSession(cookie);
        if (profileId is null)
        {
            return new { success = false, message = "无有效会话" };
        }

        try
        {
            var payload = await ReadTrackPayloadAsync(Request);
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

    /// <summary>一次性领取所有已领取普通奖励轨中由管理员后续追加的奖励。</summary>
    [HttpPost("claim-compensation")]
    public object ClaimCompensation([FromHeader(Name = "X-BP-Token")] string? token = null)
    {
        var profileId = BattlePassSession.Resolve(token);
        if (profileId is null)
        {
            return new { success = false, message = "未登录或会话已过期" };
        }

        try
        {
            var season = BattlePassStore.GetSeason();
            var (ok, message, granted) = battlePassService.ClaimCompensation(profileId, season);
            return new { success = ok, message, granted };
        }
        catch (Exception ex)
        {
            logger.Error($"[SPT-BattlePass] 补偿领取失败 profile={profileId}: {ex.Message}");
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
