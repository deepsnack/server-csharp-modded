using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SPTarkov.Server.Core.BattlePass.Administration;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.DI.Annotations;

namespace SPTarkov.Server.Core.BattlePass.Controllers;

/// <summary>
///     通行证管理端 API。鉴权统一走 <see cref="BattlePassAdminSessionService.ValidateToken"/>：
///     同时接受 WebRegister 原始 admin token（主后台 index.html / Portal SSO 直接携带）
///     与 BattlePass 会话 token（session/exchange 签发），由 <c>IsAdmin</c> 裁决身份。
///     协管（collaborator）会话在此控制器的写操作被拒绝——协管须走审核提交流程。
/// </summary>
[Injectable]
[ApiController]
[Route("battlepass/api/admin")]
public class BattlePassAdminController(
    BattlePassService battlePassService,
    ActivationCodeService activationCodeService,
    BattlePassTraderSync traderSync,
    BattlePassTrackService trackService,
    BattlePassRecipeSync recipeSync,
    TaskGeneratorService taskGenerator,
    BattlePassAdminSessionService sessionService,
    TitleChangeHandler titleChangeHandler
) : ControllerBase
{
    // 统一鉴权：任意合法管理员 token（原始或会话）均放行；协管被 IsAdmin 挡下（写操作）。
    private bool Auth(string? token) => sessionService.ValidateToken(token)?.IsAdmin == true;

    // 读放宽：管理员或持有对应 *.read 能力的协管均可读取模块数据（协管复用同一模块页浏览/编辑，
    // 保存时前端改走 /reviews/submit 进审核队列，而非本控制器的即时写端点）。
    private bool CanRead(string? token, string capability)
        => sessionService.ValidateToken(token)?.HasCapability(capability) == true;

    private static string? NormalizeRefreshScope(string? scope)
    {
        return scope?.Trim().ToLowerInvariant() switch
        {
            "daily" => "daily",
            "weekly" => "weekly",
            "season" => "season",
            "all" => "all",
            _ => null,
        };
    }

    // ---- 赛季 ----
    [HttpGet("season")]
    public object GetSeason([FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        // 赛季信息为奖励轨页的基础上下文，协管持 tracks.read 即可读取。
        if (!CanRead(token, "tracks.read"))
        {
            return new { success = false, message = "未授权" };
        }

        return new { success = true, season = BattlePassStore.GetSeason() };
    }

    [HttpPost("season")]
    public object SaveSeason([FromBody] BpSeason season, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        BattlePassStore.SaveSeason(season);
        return new { success = true };
    }

    // ---- 双轨奖励 ----
    [HttpGet("tracks")]
    public object GetTracks([FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!CanRead(token, "tracks.read"))
        {
            return new { success = false, message = "未授权" };
        }

        return new { success = true, tracks = BattlePassStore.GetTracks() };
    }

    [HttpPost("tracks")]
    public object SaveTracks(
        [FromBody] Dictionary<int, BpLevelRewards> tracks,
        [FromHeader(Name = "X-Admin-Token")] string? token = null
    )
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        BattlePassStore.SaveTracks(tracks);
        return new { success = true };
    }

    // ---- 任务模板 ----
    [HttpGet("tasks")]
    public object GetTasks([FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!CanRead(token, "tasks.read"))
        {
            return new { success = false, message = "未授权" };
        }

        return new { success = true, tasks = BattlePassStore.GetTasks() };
    }

    /// <summary>新增或更新单个任务模板（按 id upsert）。</summary>
    [HttpPost("tasks")]
    public object UpsertTask([FromBody] BpTaskTemplate task, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var validationError = BattlePassTaskRules.Validate(task);
        if (validationError is not null)
        {
            return new { success = false, message = validationError };
        }

        var tasks = BattlePassStore.GetTasks();
        task.Id = task.Id.Trim();
        tasks.RemoveAll(t => string.Equals(t.Id, task.Id, StringComparison.OrdinalIgnoreCase));
        tasks.Add(task);
        BattlePassStore.SaveTasks(tasks);
        return new { success = true };
    }

    [HttpDelete("tasks")]
    public object DeleteTask([FromBody] JsonElement request, [FromHeader(Name = "X-Admin-Token")] string? token = null)
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

        var tasks = BattlePassStore.GetTasks();
        var removed = tasks.RemoveAll(t => t.Id == id);
        BattlePassStore.SaveTasks(tasks);
        return new { success = removed > 0 };
    }

    // ---- 任务生成 ----
    [HttpGet("tasks/gen-spec")]
    public object GetGenSpec([FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!CanRead(token, "tasks.read"))
        {
            return new { success = false, message = "未授权" };
        }

        return new { success = true, spec = BattlePassStore.GetGenSpec() };
    }

    [HttpPost("tasks/gen-spec")]
    public object SaveGenSpec([FromBody] BpGenSpec spec, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        spec.Daily ??= new BpGenScopeSpec();
        spec.Weekly ??= new BpGenScopeSpec();
        spec.Season ??= new BpGenScopeSpec();
        NormalizeGenScope(spec.Daily);
        NormalizeGenScope(spec.Weekly);
        NormalizeGenScope(spec.Season);
        BattlePassStore.SaveGenSpec(spec);
        return new { success = true };
    }

    /// <summary>手动一键生成任务：按规格向<b>独立生成池</b>写入 gen_ 任务（仅启用的 scope）。可指定 scopes，缺省全部。</summary>
    [HttpPost("tasks/generate")]
    public object GenerateTasks([FromBody] JsonElement request, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        List<string>? scopes = null;
        if (request.ValueKind == JsonValueKind.Object && request.TryGetProperty("scopes", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            scopes = arr.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList();
        }

        var generated = taskGenerator.Generate(scopes);
        return new { success = true, generated };
    }

    /// <summary>查看独立生成池（gen_ 自动/手动生成任务），与管理员自定义任务池分开，便于核对与清理。</summary>
    [HttpGet("tasks/generated")]
    public object GetGeneratedTasks([FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        return new { success = true, tasks = BattlePassStore.GetGenTasks() };
    }

    /// <summary>清空生成池：手动一键清除全部 gen_ 生成任务，避免长期累积。不影响自定义任务池。</summary>
    [HttpDelete("tasks/generated")]
    public object ClearGeneratedTasks([FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var cleared = BattlePassStore.GetGenTasks().Count;
        BattlePassStore.SaveGenTasks(new List<BpTaskTemplate>());
        return new { success = true, cleared };
    }

    // ---- 网页商店自定义货架 ----
    [HttpGet("shop")]
    public object GetShopOffers([FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!CanRead(token, "shop.read"))
        {
            return new { success = false, message = "未授权" };
        }

        var pools = BattlePassStore.GetLotteryPools()
            .OrderBy(p => p.SortOrder)
            .Select(p => new { id = p.Id, name = p.Name })
            .ToList();
        return new
        {
            success = true,
            offers = BattlePassStore.GetShopOffers(),
            refreshSeconds = BattlePassStore.GetShopState().RefreshSeconds,
            pools,
        };
    }

    /// <summary>设置网页商店刷新周期（秒，&lt;=0 = 不刷新）。修改后从当前时刻起算新周期。</summary>
    [HttpPost("shop/refresh-period")]
    public object SetShopRefreshPeriod([FromBody] JsonElement request, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var seconds = request.TryGetProperty("seconds", out var s) && s.TryGetInt32(out var v) ? v : 0;
        seconds = Math.Max(0, seconds);

        var state = BattlePassStore.GetShopState();
        state.RefreshSeconds = seconds;
        // 改周期即从现在起算；不立即清库存/限购，到点自然滚动
        state.PeriodStartUtc = seconds > 0 ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : 0;
        BattlePassStore.SaveShopState(state);
        return new { success = true, refreshSeconds = seconds };
    }

    /// <summary>新增或更新单个网页商店自定义货架项（按 id upsert）。</summary>
    [HttpPost("shop")]
    public object UpsertShopOffer([FromBody] BpTraderOffer offer, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        if (string.IsNullOrWhiteSpace(offer.Id))
        {
            return new { success = false, message = "货架项 id 不能为空" };
        }

        offer.Id = offer.Id.Trim();
        offer.RewardType = NormalizeShopRewardType(offer.RewardType);
        var isVirtual = BattlePassShopService.IsVirtualRewardType(offer.RewardType);

        if (isVirtual)
        {
            // 虚拟商品（抽奖券/兑换币）不邮寄实物，tpl 无意义，占位为空
            offer.Tpl = "";
            if (string.Equals(offer.RewardType, "lotteryPoolTickets", StringComparison.OrdinalIgnoreCase))
            {
                offer.PoolId = offer.PoolId?.Trim();
                if (string.IsNullOrWhiteSpace(offer.PoolId))
                {
                    return new { success = false, message = "限定抽奖券必须选择绑定奖池" };
                }

                var exists = BattlePassStore.GetLotteryPools()
                    .Any(pool => string.Equals(pool.Id, offer.PoolId, StringComparison.OrdinalIgnoreCase));
                if (!exists)
                {
                    return new { success = false, message = "绑定奖池不存在" };
                }
            }
            else
            {
                offer.PoolId = null;
            }
        }
        else
        {
            offer.Tpl = offer.Tpl.Trim();
            offer.PoolId = null;
            if (!Models.Common.MongoId.IsValidMongoId(offer.Tpl))
            {
                return new { success = false, message = "商品 tpl 无效" };
            }
        }

        offer.Cost ??= new List<BpBarterCost>();
        if (offer.Cost.Any(c => c is null || !Models.Common.MongoId.IsValidMongoId(c.Tpl?.Trim()) || c.Count <= 0))
        {
            return new { success = false, message = "支付物品 tpl 无效或数量不是正整数" };
        }

        foreach (var cost in offer.Cost)
        {
            cost.Tpl = cost.Tpl.Trim();
        }

        offer.SellCount = Math.Max(1, offer.SellCount);
        offer.BuyLimit = Math.Max(0, offer.BuyLimit);
        // RefreshSeconds：null=继承全局；否则夹到 >=0（0=本商品终身累计）
        if (offer.RefreshSeconds.HasValue)
        {
            offer.RefreshSeconds = Math.Max(0, offer.RefreshSeconds.Value);
        }

        var offers = BattlePassStore.GetShopOffers();
        var existing = offers.FirstOrDefault(o => string.Equals(o.Id, offer.Id, StringComparison.OrdinalIgnoreCase));
        // 库存改动、或刷新周期模式改动，都清空该商品累计销量，避免旧账本误判售罄
        if (existing is not null && (existing.Stock != offer.Stock || existing.RefreshSeconds != offer.RefreshSeconds))
        {
            ResetShopSales($"custom:{offer.Id}");
        }

        offers.RemoveAll(o => string.Equals(o.Id, offer.Id, StringComparison.OrdinalIgnoreCase));
        offers.Add(offer);
        BattlePassStore.SaveShopOffers(offers);
        return new { success = true };
    }

    /// <summary>规范化网页商店发放方式（大小写容错，未知值回退 item）。</summary>
    private static string NormalizeShopRewardType(string? rewardType)
    {
        return (rewardType ?? "item").Trim().ToLowerInvariant() switch
        {
            "lotteryglobaltickets" => "lotteryGlobalTickets",
            "lotterypooltickets" => "lotteryPoolTickets",
            "lotteryexchangecoins" => "lotteryExchangeCoins",
            _ => "item",
        };
    }

    [HttpDelete("shop")]
    public object DeleteShopOffer([FromBody] JsonElement request, [FromHeader(Name = "X-Admin-Token")] string? token = null)
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

        var offers = BattlePassStore.GetShopOffers();
        var removed = offers.RemoveAll(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));
        BattlePassStore.SaveShopOffers(offers);
        if (removed > 0)
        {
            ResetShopSales($"custom:{id}");
        }

        return new { success = removed > 0 };
    }

    private static void NormalizeGenScope(BpGenScopeSpec scope)
    {
        scope.Count = Math.Clamp(scope.Count, 1, 100);
        scope.AutoPeriodHours = Math.Max(0, scope.AutoPeriodHours);
        scope.ConditionTypes = (scope.ConditionTypes ?? [])
            .Where(t => t is not null && (t.Equals("Kills", StringComparison.OrdinalIgnoreCase)
                || t.Equals("Exploration", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        scope.KillTargets = (scope.KillTargets ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        scope.Locations = (scope.Locations ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        scope.MinCount = Math.Max(1, scope.MinCount);
        scope.MaxCount = Math.Max(scope.MinCount, scope.MaxCount);
        scope.XpEasy = Math.Max(0, scope.XpEasy);
        scope.XpMed = Math.Max(0, scope.XpMed);
        scope.XpHard = Math.Max(0, scope.XpHard);
    }

    private static void ResetShopSales(string offerKey)
    {
        var state = BattlePassStore.GetShopState();
        var salesRemoved = state.Sales.Remove(offerKey);
        var periodRemoved = state.OfferPeriods.Remove(offerKey);
        if (salesRemoved || periodRemoved)
        {
            BattlePassStore.SaveShopState(state);
        }
    }

    [HttpPost("tasks/refresh")]
    public object RefreshActiveTasks([FromBody] JsonElement request, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var scope = request.TryGetProperty("scope", out var s) ? s.GetString() ?? "daily" : "daily";
        scope = NormalizeRefreshScope(scope);
        if (scope is null)
        {
            return new { success = false, message = "scope 仅支持 daily / weekly / season / all" };
        }

        var profileId = request.TryGetProperty("profileId", out var p) ? p.GetString() : null;
        var all = request.TryGetProperty("all", out var a) && a.ValueKind == JsonValueKind.True;
        var profileIds = all || string.IsNullOrWhiteSpace(profileId)
            ? BattlePassStore.ListProgressProfileIds()
            : new List<string> { profileId.Trim() };

        var season = BattlePassStore.GetSeason();
        var refreshed = 0;
        var skipped = 0;

        foreach (var pid in profileIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var prog = battlePassService.GetOrResetProgress(pid, season);
            var changed = trackService.ForceRefreshTasks(pid, prog, scope);
            if (changed)
            {
                BattlePassStore.SaveProgress(pid, prog);
            }

            if (changed)
            {
                refreshed++;
            }
            else
            {
                skipped++;
            }
        }

        return new { success = true, refreshed, skipped };
    }

    // ---- 激活码 ----
    [HttpPost("codes/generate")]
    public object GenerateCodes(
        [FromBody] JsonElement request,
        [FromHeader(Name = "X-Admin-Token")] string? token = null
    )
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var type = request.TryGetProperty("type", out var ty) ? ty.GetString() ?? "premium" : "premium";
        var value = request.TryGetProperty("value", out var v) ? v.GetInt32() : 0;
        var count = request.TryGetProperty("count", out var c) ? c.GetInt32() : 1;
        var batchTag = request.TryGetProperty("batchTag", out var b) ? b.GetString() : null;
        var poolId = request.TryGetProperty("poolId", out var p) ? p.GetString() : null;
        var expiresUtc = request.TryGetProperty("expiresUtc", out var e) ? e.GetInt64() : 0;
        var maxRedemptions = request.TryGetProperty("maxRedemptions", out var m) ? m.GetInt32() : 1;
        var perPlayerOnce = !request.TryGetProperty("perPlayerOnce", out var once) || once.GetBoolean();
        var commonCode = request.TryGetProperty("commonCode", out var common) && common.GetBoolean();

        List<BpReward>? rewards = null;
        if (request.TryGetProperty("rewards", out var rw) && rw.ValueKind == JsonValueKind.Array)
        {
            rewards = rw.Deserialize<List<BpReward>>();
        }

        if (string.Equals(type, "rewards", StringComparison.OrdinalIgnoreCase) && rewards is not { Count: > 0 })
        {
            return new { success = false, message = "自定义奖励码必须至少配置一项奖励" };
        }

        if (string.Equals(type, "lotteryPoolTickets", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(poolId))
            {
                return new { success = false, message = "限定抽奖券必须选择绑定奖池" };
            }

            var exists = BattlePassStore.GetLotteryPools()
                .Any(pool => string.Equals(pool.Id, poolId, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                return new { success = false, message = "绑定奖池不存在" };
            }
        }

        var created = activationCodeService.Generate(type, value, count, batchTag, poolId, expiresUtc, maxRedemptions, perPlayerOnce, commonCode, rewards);
        return new { success = true, codes = created.Select(c2 => c2.Code).ToList(), entries = created };
    }

    [HttpGet("codes")]
    public object GetCodes([FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        return new { success = true, codes = BattlePassStore.GetCodes() };
    }

    [HttpGet("codes/export")]
    public IActionResult ExportCodes([FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new UnauthorizedResult();
        }

        return CodesCsv(BattlePassStore.GetCodes(), "battlepass-codes.csv");
    }

    [HttpPost("codes/export")]
    public IActionResult ExportSelectedCodes([FromBody] JsonElement request, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new UnauthorizedResult();
        }

        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (request.ValueKind == JsonValueKind.Object
            && request.TryGetProperty("codes", out var codes)
            && codes.ValueKind == JsonValueKind.Array)
        {
            foreach (var code in codes.EnumerateArray())
            {
                var value = code.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    selected.Add(value.Trim());
                }
            }
        }

        var entries = BattlePassStore.GetCodes();
        if (selected.Count > 0)
        {
            entries = entries
                .Where(c => selected.Contains(c.Code))
                .ToList();
        }

        return CodesCsv(entries, selected.Count > 0 ? "battlepass-selected-codes.csv" : "battlepass-codes.csv");
    }

    private IActionResult CodesCsv(IEnumerable<BpActivationCode> codes, string fileName)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("code,type,value,poolId,batchTag,createdUtc,expiresUtc,maxRedemptions,redeemCount,perPlayerOnce,redeemedBy,redeemedUtc");
        foreach (var c in codes.OrderByDescending(c => c.CreatedUtc))
        {
            sb.AppendLine(string.Join(",", [
                Csv(c.Code),
                Csv(c.Type),
                c.Value.ToString(),
                Csv(c.PoolId),
                Csv(c.BatchTag),
                c.CreatedUtc.ToString(),
                c.ExpiresUtc.ToString(),
                c.MaxRedemptions.ToString(),
                c.RedeemCount.ToString(),
                c.PerPlayerOnce.ToString(),
                Csv(c.RedeemedBy),
                c.RedeemedUtc?.ToString() ?? "",
            ]));
        }

        return File(System.Text.Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", fileName);
    }

    // ---- 通行证商人：元信息 ----
    // ---- 自定义藏身处配方 ----
    [HttpGet("custom-recipes")]
    public object GetCustomRecipes([FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!CanRead(token, "recipes.read"))
        {
            return new { success = false, message = "未授权" };
        }

        return new { success = true, recipes = BattlePassStore.GetCustomRecipes() };
    }

    /// <summary>创建或更新自定义配方（带 id=更新；不带=创建并生成 production id）。保存后热注入 DB。</summary>
    [HttpPost("custom-recipes")]
    public object SaveCustomRecipe([FromBody] BpCustomRecipe recipe, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        if (string.IsNullOrWhiteSpace(recipe.EndProduct) || !Models.Common.MongoId.IsValidMongoId(recipe.EndProduct))
        {
            return new { success = false, message = "产物 tpl 无效" };
        }

        if (recipe.Ingredients.Count == 0)
        {
            return new { success = false, message = "至少需要一种原料" };
        }

        if (recipe.Ingredients.Any(i => !Models.Common.MongoId.IsValidMongoId(i.Tpl)))
        {
            return new { success = false, message = "存在无效的原料 tpl" };
        }

        if (recipe.ProductionTime < 1)
        {
            return new { success = false, message = "制作时长必须大于 0" };
        }

        var recipes = BattlePassStore.GetCustomRecipes();
        if (string.IsNullOrWhiteSpace(recipe.Id))
        {
            recipe.Id = new Models.Common.MongoId().ToString();
            recipes.Add(recipe);
        }
        else
        {
            var idx = recipes.FindIndex(r => r.Id == recipe.Id);
            if (idx < 0)
            {
                recipes.Add(recipe);
            }
            else
            {
                recipes[idx] = recipe;
            }
        }

        BattlePassStore.SaveCustomRecipes(recipes);
        recipeSync.Sync(); // 热重注入：新配方即刻可在 query/recipes 搜到、被奖励轨引用
        return new { success = true, id = recipe.Id };
    }

    [HttpDelete("custom-recipes/{id}")]
    public object DeleteCustomRecipe(string id, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var recipes = BattlePassStore.GetCustomRecipes();
        var removed = recipes.RemoveAll(r => r.Id == id);
        if (removed > 0)
        {
            BattlePassStore.SaveCustomRecipes(recipes);
            recipeSync.Sync();
        }

        return new { success = removed > 0 };
    }

    [HttpGet("trader-meta")]
    public object GetTraderMeta([FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        // 商人元信息为货架页基础上下文（货币展示等），协管持 trader.read 即可读取。
        if (!CanRead(token, "trader.read"))
        {
            return new { success = false, message = "未授权" };
        }

        return new { success = true, config = BattlePassStore.GetTraderConfig() };
    }

    [HttpPost("trader-meta")]
    public object SaveTraderMeta(
        [FromBody] BpTraderConfig config,
        [FromHeader(Name = "X-Admin-Token")] string? token = null
    )
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var currency = config.Currency?.Trim().ToUpperInvariant();
        if (
            !Enum.TryParse<Models.Enums.CurrencyType>(currency, out var parsedCurrency)
            || !Enum.IsDefined(parsedCurrency)
        )
        {
            return new { success = false, message = "主货币无效" };
        }

        if (config.ResupplySeconds < 60)
        {
            return new { success = false, message = "补货周期不能短于 60 秒" };
        }

        config.Currency = currency!;
        BattlePassStore.SaveTraderConfig(config);
        traderSync.Sync(); // 热重注入（客户端商人界面可能需重进/重登刷新元信息与头像）
        return new { success = true };
    }

    /// <summary>上传商人头像：body { image: "data:image/png;base64,..." 或裸 base64 }。仅 PNG/JPG，≤2MB。</summary>
    [HttpPost("trader-avatar")]
    public object UploadTraderAvatar(
        [FromBody] JsonElement request,
        [FromHeader(Name = "X-Admin-Token")] string? token = null
    )
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var image = request.TryGetProperty("image", out var im) ? im.GetString() : null;
        if (string.IsNullOrWhiteSpace(image))
        {
            return new { success = false, message = "缺少 image" };
        }

        // 去掉 data URL 前缀
        var comma = image.IndexOf(',');
        if (image.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
        {
            image = image[(comma + 1)..];
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(image.Trim());
        }
        catch
        {
            return new { success = false, message = "图片 base64 解析失败" };
        }

        if (bytes.Length == 0 || bytes.Length > 2 * 1024 * 1024)
        {
            return new { success = false, message = "图片为空或超过 2MB" };
        }

        // 按魔数判定类型，决定扩展名（保证 serve 时 content-type 与内容一致）
        string ext;
        if (bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            ext = ".png";
        }
        else if (bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            ext = ".jpg";
        }
        else
        {
            return new { success = false, message = "仅支持 PNG 或 JPG 图片" };
        }

        var fileName = "trader-avatar" + ext;
        try
        {
            // 清掉旧的另一种扩展名，避免残留
            foreach (var stale in new[] { "trader-avatar.png", "trader-avatar.jpg" })
            {
                var p = BattlePassStore.TraderAvatarPath(stale);
                if (System.IO.File.Exists(p) && !string.Equals(stale, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    System.IO.File.Delete(p);
                }
            }

            System.IO.File.WriteAllBytes(BattlePassStore.TraderAvatarPath(fileName), bytes);
        }
        catch (Exception ex)
        {
            return new { success = false, message = "保存失败: " + ex.Message };
        }

        var cfg = BattlePassStore.GetTraderConfig();
        cfg.AvatarFile = fileName;
        BattlePassStore.SaveTraderConfig(cfg);
        traderSync.Sync();
        return new { success = true, avatarFile = fileName };
    }

    // ---- 通行证商人：货架 offers ----
    [HttpGet("offers")]
    public object GetOffers([FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        // 读放宽：协管持 trader.read 可浏览货架；保存改走 /reviews/submit。
        if (!CanRead(token, "trader.read"))
        {
            return new { success = false, message = "未授权" };
        }

        return new { success = true, offers = BattlePassStore.GetOffers() };
    }

    /// <summary>新增或更新单个货架 offer（按 id upsert）。</summary>
    [HttpPost("offers")]
    public object UpsertOffer([FromBody] BpTraderOffer offer, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        if (string.IsNullOrWhiteSpace(offer.Id))
        {
            return new { success = false, message = "货架项 id 不能为空" };
        }

        if (string.IsNullOrWhiteSpace(offer.Tpl))
        {
            return new { success = false, message = "商品 tpl 不能为空" };
        }

        offer.Id = offer.Id.Trim();
        offer.Tpl = offer.Tpl.Trim();
        if (!Models.Common.MongoId.IsValidMongoId(offer.Tpl))
        {
            return new { success = false, message = "商品 tpl 无效" };
        }

        offer.Cost ??= new List<BpBarterCost>();
        if (offer.Cost.Any(c => c is null || !Models.Common.MongoId.IsValidMongoId(c.Tpl) || c.Count <= 0))
        {
            return new { success = false, message = "支付物品 tpl 无效或数量不是正整数" };
        }

        foreach (var cost in offer.Cost)
        {
            cost.Tpl = cost.Tpl.Trim();
        }

        var offers = BattlePassStore.GetOffers();
        var existing = offers.FirstOrDefault(o => string.Equals(o.Id, offer.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && existing.Stock != offer.Stock)
        {
            ResetShopSales($"trader:{offer.Id}");
        }

        offers.RemoveAll(o => string.Equals(o.Id, offer.Id, StringComparison.OrdinalIgnoreCase));
        offers.Add(offer);
        BattlePassStore.SaveOffers(offers);
        traderSync.Sync();
        return new { success = true };
    }

    [HttpDelete("offers")]
    public object DeleteOffer([FromBody] JsonElement request, [FromHeader(Name = "X-Admin-Token")] string? token = null)
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

        var offers = BattlePassStore.GetOffers();
        var removed = offers.RemoveAll(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));
        BattlePassStore.SaveOffers(offers);
        if (removed > 0)
        {
            ResetShopSales($"trader:{id}");
        }

        traderSync.Sync();
        return new { success = removed > 0 };
    }

    // ---- 通行证称号：目录 ----
    [HttpGet("titles")]
    public object GetTitles([FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!CanRead(token, "titles.read"))
        {
            return new { success = false, message = "未授权" };
        }

        return new { success = true, titles = BattlePassStore.GetTitleCatalog() };
    }

    /// <summary>新增或更新单个称号目录项（按 id upsert）。</summary>
    [HttpPost("titles")]
    public object UpsertTitle([FromBody] JsonElement request, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        return TitleCommandResponse(ApplyTitleCommand("title.upsert", request, token, "已保存"));
    }

    [HttpDelete("titles")]
    public object DeleteTitle([FromBody] JsonElement request, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        return TitleCommandResponse(ApplyTitleCommand("title.delete", request, token, "已删除"));
    }

    /// <summary>上传图片称号的 PNG：body { id, image: "data:image/png;base64,..." 或裸 base64 }。仅 PNG、约定 128×32、≤512KB。</summary>
    [HttpPost("title-image")]
    public object UploadTitleImage(
        [FromBody] JsonElement request,
        [FromHeader(Name = "X-Admin-Token")] string? token = null
    )
    {
        var result = ApplyTitleCommand("title.image", request, token, "图片已上传");
        var id = request.TryGetProperty("id", out var i) ? i.GetString() : null;
        return new { success = result.Success, message = result.Message, revision = result.Revision, imageUrl = id is null ? null : $"/battlepass/api/title-image/{id}" };
    }

    // ---- 通行证称号：授予 / 撤销 / 持有总览 ----
    [HttpPost("titles/grant")]
    public object GrantTitle([FromBody] JsonElement request, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        return TitleCommandResponse(ApplyTitleCommand("title.grant", request, token, "已授予"));
    }

    [HttpPost("titles/revoke")]
    public object RevokeTitle([FromBody] JsonElement request, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        return TitleCommandResponse(ApplyTitleCommand("title.revoke", request, token, "已撤销"));
    }

    [HttpGet("title-holders")]
    public object GetTitleHolders([FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!CanRead(token, "titles.read"))
        {
            return new { success = false, message = "未授权" };
        }

        var holders = BattlePassStore
            .ListPlayerTitleProfileIds()
            .Select(pid =>
            {
                var t = BattlePassStore.GetPlayerTitles(pid);
                return new
                {
                    profileId = pid,
                    username = battlePassService.GetUsername(pid),
                    owned = t.Owned.ToList(),
                    equipped = t.Equipped,
                };
            })
            .ToList();

        return new { success = true, holders };
    }

    private static object TitleCommandResponse((bool Success, string Message, string? Revision) result)
    {
        return new { success = result.Success, message = result.Message, revision = result.Revision };
    }

    private (bool Success, string Message, string? Revision) ApplyTitleCommand(string commandType, JsonElement request, string? token, string successMessage)
    {
        if (!Auth(token))
        {
            return (false, "未授权", null);
        }

        try
        {
            var normalized = titleChangeHandler.Normalize(commandType, request);
            var error = titleChangeHandler.Validate(commandType, normalized);
            if (error is not null)
            {
                return (false, error, null);
            }

            var revision = titleChangeHandler.ApplyAndActivate(commandType, normalized, expectedBaseRevision: null, changeId: null);
            return (true, successMessage, revision);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    // ---- 玩家总览 ----
    [HttpPost("players/reset-progress")]
    public object ResetPlayerProgress([FromBody] JsonElement request, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var preservePremium = !request.TryGetProperty("preservePremium", out var pp) || pp.ValueKind != JsonValueKind.False;
        var all = request.TryGetProperty("all", out var a) && a.ValueKind == JsonValueKind.True;
        var profileIds = new List<string>();

        if (all)
        {
            profileIds = battlePassService
                .ListProfileIds()
                .Concat(BattlePassStore.ListProgressProfileIds())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Where(x => !battlePassService.IsHeadlessProfile(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        else
        {
            if (request.TryGetProperty("profileIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
            {
                profileIds.AddRange(ids.EnumerateArray().Select(x => x.GetString()).OfType<string>().Where(x => !string.IsNullOrWhiteSpace(x)));
            }

            if (request.TryGetProperty("profileId", out var id) && id.ValueKind == JsonValueKind.String)
            {
                var single = id.GetString();
                if (!string.IsNullOrWhiteSpace(single))
                {
                    profileIds.Add(single);
                }
            }

            profileIds = profileIds
                .Where(pid => !battlePassService.IsHeadlessProfile(pid))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (profileIds.Count == 0)
        {
            return new { success = false, message = "未选择玩家" };
        }

        var season = BattlePassStore.GetSeason();
        var reset = 0;
        var revokedPurchaseRights = 0;
        var revokedRecipes = 0;
        foreach (var pid in profileIds)
        {
            var oldProgress = BattlePassStore.GetProgress(pid);
            revokedPurchaseRights += battlePassService.RevokePurchaseRights(pid, oldProgress);
            revokedRecipes += battlePassService.RevokeRecipes(pid, oldProgress);
            var progress = BattlePassStore.ResetProgress(pid, season, preservePremium);
            if (trackService.RefreshActiveTasks(pid, progress))
            {
                BattlePassStore.SaveProgress(pid, progress);
            }

            reset++;
        }

        return new
        {
            success = true,
            reset,
            preservePremium,
            revokedPurchaseRights,
            revokedRecipes,
            message = $"已重置 {reset} 个玩家的完整通行证进度，收回 {revokedPurchaseRights} 项商人购买权和 {revokedRecipes} 个配方",
        };
    }

    [HttpGet("players")]
    public object GetPlayers([FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token) && !CanRead(token, "titles.read"))
        {
            return new { success = false, message = "未授权" };
        }

        var players = battlePassService
            .ListProfileIds()
            .Concat(BattlePassStore.ListProgressProfileIds())
            .Where(pid => !string.IsNullOrWhiteSpace(pid))
            .Where(pid => !battlePassService.IsHeadlessProfile(pid))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(pid =>
            {
                var prog = BattlePassStore.GetProgress(pid);
                return new
                {
                    profileId = pid,
                    username = battlePassService.GetUsername(pid),
                    nickname = battlePassService.GetNickname(pid),
                    prog.Level,
                    prog.Xp,
                    prog.PremiumUnlocked,
                    prog.SeasonId,
                };
            })
            .ToList();

        return new { success = true, players };
    }

    private static string Csv(string? value)
    {
        value ??= "";
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
