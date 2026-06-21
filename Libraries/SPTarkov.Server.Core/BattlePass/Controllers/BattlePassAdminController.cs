using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.DI.Annotations;

namespace SPTarkov.Server.Core.BattlePass.Controllers;

/// <summary>
///     通行证管理端 API。鉴权复用 WebRegister 的 admin 域（X-Admin-Token / Portal SSO）——
///     管理页先调 /register/api/admin/login 取 token，再带 X-Admin-Token 访问本控制器。
/// </summary>
[Injectable]
[ApiController]
[Route("battlepass/api/admin")]
public class BattlePassAdminController(
    BattlePassService battlePassService,
    ActivationCodeService activationCodeService,
    BattlePassTraderSync traderSync,
    BattlePassTrackService trackService,
    BattlePassRecipeSync recipeSync
)
{
    private static bool Auth(string? token) => WebRegisterController.IsAdminAuthorized(token);

    // 称号 id 安全 slug（同时是图片文件名约束，杜绝路径穿越）。
    private static readonly Regex TitleIdRegex = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

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
        if (!Auth(token))
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
        if (!Auth(token))
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
        if (!Auth(token))
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

        if (string.IsNullOrWhiteSpace(task.Id))
        {
            return new { success = false, message = "任务 id 不能为空" };
        }

        if (
            string.Equals(task.ConditionType, "Kills", StringComparison.OrdinalIgnoreCase)
            && ((task.EnemyEquipment?.Count ?? 0) > 0 || (task.PlayerEquipment?.Count ?? 0) > 0 || (task.WeaponMods?.Count ?? 0) > 0)
        )
        {
            return new { success = false, message = "战后记录不含敌我装备和武器改件，无法作为击杀任务条件" };
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

        var created = activationCodeService.Generate(type, value, count, batchTag);
        return new { success = true, codes = created.Select(c2 => c2.Code).ToList() };
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

    // ---- 通行证商人：元信息 ----
    // ---- 自定义藏身处配方 ----
    [HttpGet("custom-recipes")]
    public object GetCustomRecipes([FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
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
        if (!Auth(token))
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
                if (File.Exists(p) && !string.Equals(stale, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(p);
                }
            }

            File.WriteAllBytes(BattlePassStore.TraderAvatarPath(fileName), bytes);
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
        if (!Auth(token))
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
        offers.RemoveAll(o => o.Id == offer.Id);
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
        var removed = offers.RemoveAll(o => o.Id == id);
        BattlePassStore.SaveOffers(offers);
        traderSync.Sync();
        return new { success = removed > 0 };
    }

    // ---- 通行证称号：目录 ----
    [HttpGet("titles")]
    public object GetTitles([FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        return new { success = true, titles = BattlePassStore.GetTitleCatalog() };
    }

    /// <summary>新增或更新单个称号目录项（按 id upsert）。</summary>
    [HttpPost("titles")]
    public object UpsertTitle([FromBody] BpTitle title, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        if (string.IsNullOrWhiteSpace(title.Id))
        {
            return new { success = false, message = "称号 id 不能为空" };
        }

        if (!TitleIdRegex.IsMatch(title.Id))
        {
            return new { success = false, message = "称号 id 仅允许字母/数字/下划线/连字符（≤64 位）" };
        }

        var titles = BattlePassStore.GetTitleCatalog();
        titles.RemoveAll(t => t.Id == title.Id);
        titles.Add(title);
        BattlePassStore.SaveTitleCatalog(titles);
        return new { success = true };
    }

    [HttpDelete("titles")]
    public object DeleteTitle([FromBody] JsonElement request, [FromHeader(Name = "X-Admin-Token")] string? token = null)
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

        var titles = BattlePassStore.GetTitleCatalog();
        var removed = titles.RemoveAll(t => t.Id == id);
        BattlePassStore.SaveTitleCatalog(titles);

        // 一并清理图片文件（若有）
        try
        {
            var p = BattlePassStore.TitleImagePath(id);
            if (File.Exists(p))
            {
                File.Delete(p);
            }
        }
        catch
        {
            // 忽略
        }

        return new { success = removed > 0 };
    }

    /// <summary>上传图片称号的 PNG：body { id, image: "data:image/png;base64,..." 或裸 base64 }。仅 PNG、约定 128×32、≤512KB。</summary>
    [HttpPost("title-image")]
    public object UploadTitleImage(
        [FromBody] JsonElement request,
        [FromHeader(Name = "X-Admin-Token")] string? token = null
    )
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var id = request.TryGetProperty("id", out var i) ? i.GetString() : null;
        if (string.IsNullOrWhiteSpace(id) || !TitleIdRegex.IsMatch(id))
        {
            return new { success = false, message = "称号 id 非法（先保存称号目录项再上传图片）" };
        }

        var image = request.TryGetProperty("image", out var im) ? im.GetString() : null;
        if (string.IsNullOrWhiteSpace(image))
        {
            return new { success = false, message = "缺少 image" };
        }

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

        if (bytes.Length == 0 || bytes.Length > 512 * 1024)
        {
            return new { success = false, message = "图片为空或超过 512KB" };
        }

        // 必须是 PNG（魔数 89 50 4E 47 0D 0A 1A 0A），且 IHDR 尺寸 == 128×32（约定横幅，透明底）
        if (bytes.Length < 24 || bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47)
        {
            return new { success = false, message = "仅支持 PNG 图片" };
        }

        // IHDR：PNG 签名 8 字节后是 length(4)+"IHDR"(4)+width(4 大端)+height(4 大端)
        var width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
        var height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
        if (width != 128 || height != 32)
        {
            return new { success = false, message = $"图片尺寸必须为约定的 128×32（当前 {width}×{height}）" };
        }

        try
        {
            Directory.CreateDirectory(BattlePassStore.TitleImageDir);
            File.WriteAllBytes(BattlePassStore.TitleImagePath(id), bytes);
        }
        catch (Exception ex)
        {
            return new { success = false, message = "保存失败: " + ex.Message };
        }

        // 同步目录项：确保该称号 type=image、尺寸记录正确
        var titles = BattlePassStore.GetTitleCatalog();
        var t = titles.FirstOrDefault(x => x.Id == id);
        if (t is not null)
        {
            t.Type = "image";
            t.ImageFile = id + ".png";
            t.Width = 128;
            t.Height = 32;
            BattlePassStore.SaveTitleCatalog(titles);
        }

        return new { success = true, imageUrl = $"/battlepass/api/title-image/{id}" };
    }

    // ---- 通行证称号：授予 / 撤销 / 持有总览 ----
    [HttpPost("titles/grant")]
    public object GrantTitle([FromBody] JsonElement request, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var profileId = request.TryGetProperty("profileId", out var p) ? p.GetString() : null;
        var titleId = request.TryGetProperty("titleId", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(titleId))
        {
            return new { success = false, message = "缺少 profileId 或 titleId" };
        }

        var ok = BattlePassStore.GrantTitle(profileId, titleId);
        return new { success = true, granted = ok, message = ok ? "已授予" : "该玩家已拥有此称号" };
    }

    [HttpPost("titles/revoke")]
    public object RevokeTitle([FromBody] JsonElement request, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var profileId = request.TryGetProperty("profileId", out var p) ? p.GetString() : null;
        var titleId = request.TryGetProperty("titleId", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(titleId))
        {
            return new { success = false, message = "缺少 profileId 或 titleId" };
        }

        var ok = BattlePassStore.RevokeTitle(profileId, titleId);
        return new { success = true, revoked = ok, message = ok ? "已撤销" : "该玩家未拥有此称号" };
    }

    [HttpGet("title-holders")]
    public object GetTitleHolders([FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
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
        if (!Auth(token))
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
}
