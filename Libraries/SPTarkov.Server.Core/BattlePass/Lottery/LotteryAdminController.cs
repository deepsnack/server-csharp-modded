using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.BattlePass.Administration;
using SPTarkov.Server.Core.Controllers;

namespace SPTarkov.Server.Core.BattlePass.Controllers;

/// <summary>通行证抽奖后台 API。鉴权统一走
/// <see cref="BattlePassAdminSessionService.ValidateToken"/>（兼容原始 token 与会话 token）。</summary>
[Injectable]
[ApiController]
[Route("battlepass/api/admin/lottery")]
public class LotteryAdminController(
    LotteryService lotteryService,
    LotteryWalletService walletService,
    BattlePassService battlePassService,
    BattlePassAdminSessionService sessionService
) : ControllerBase
{
    private const long MaxUploadBytes = 5 * 1024 * 1024;
    private static readonly HashSet<string> AllowedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".webp",
        ".gif",
    };
    private static string LotteryUploadDir => Path.Combine(Directory.GetCurrentDirectory(), "SPT_Data", "battlepass", "page", "uploads", "lottery");

    private bool Auth(string? token) => sessionService.ValidateToken(token)?.IsAdmin == true;

    // 读放宽：管理员或持 lottery.read 的协管可读（协管复用抽奖页浏览/编辑，保存改走 /reviews/submit）。
    private bool CanRead(string? token) => sessionService.ValidateToken(token)?.HasCapability("lottery.read") == true;

    [HttpGet("settings")]
    public object GetSettings([FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!CanRead(token))
        {
            return new { success = false, message = "未授权" };
        }

        return new { success = true, settings = BattlePassStore.GetLotterySettings() };
    }

    [HttpPost("settings")]
    public object SaveSettings([FromBody] BpLotterySettings settings, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        BattlePassStore.SaveLotterySettings(settings);
        Audit("settings", "save", "lottery-settings", "保存抽奖全局设置");
        return new { success = true };
    }

    [HttpGet("pools")]
    public object GetPools([FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!CanRead(token))
        {
            return new { success = false, message = "未授权" };
        }

        var pools = BattlePassStore.GetLotteryPools().OrderBy(p => p.SortOrder).ToList();
        return new { success = true, pools };
    }

    [HttpGet("pools/{poolId}")]
    public object GetPool(string poolId, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!CanRead(token))
        {
            return new { success = false, message = "未授权" };
        }

        var pool = lotteryService.GetPool(poolId);
        return pool is null
            ? new { success = false, message = "奖池不存在" }
            : new { success = true, pool, validation = lotteryService.ValidateForPublish(pool) };
    }

    [HttpPost("pools")]
    public object UpsertPool([FromBody] BpLotteryPool pool, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (string.IsNullOrWhiteSpace(pool.Id))
        {
            pool.Id = "lottery_" + Guid.NewGuid().ToString("N")[..12];
        }

        if (string.IsNullOrWhiteSpace(pool.Status))
        {
            pool.Status = BpLotteryConstants.PoolStatusDraft;
        }

        var pools = BattlePassStore.GetLotteryPools();
        var existing = pools.FirstOrDefault(p => string.Equals(p.Id, pool.Id, StringComparison.OrdinalIgnoreCase));
        pool.CreatedUtc = existing?.CreatedUtc > 0 ? existing.CreatedUtc : now;
        pool.UpdatedUtc = now;

        if (existing is null)
        {
            pools.Add(pool);
        }
        else
        {
            pools[pools.IndexOf(existing)] = pool;
        }

        BattlePassStore.SaveLotteryPools(pools);
        Audit("pool", existing is null ? "create" : "save", pool.Id, $"保存奖池：{pool.Name}", poolId: pool.Id);
        return new { success = true, pool, validation = lotteryService.ValidateForPublish(pool) };
    }

    [HttpPost("pools/{poolId}/publish")]
    public object PublishPool(string poolId, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var pools = BattlePassStore.GetLotteryPools();
        var pool = pools.FirstOrDefault(p => string.Equals(p.Id, poolId, StringComparison.OrdinalIgnoreCase));
        if (pool is null)
        {
            return new { success = false, message = "奖池不存在" };
        }

        var errors = lotteryService.ValidateForPublish(pool);
        if (errors.Count > 0)
        {
            return new { success = false, message = "发布校验失败", errors };
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var (startUtc, _) = lotteryService.GetPoolWindow(pool);
        pool.Status = startUtc > now ? BpLotteryConstants.PoolStatusScheduled : BpLotteryConstants.PoolStatusActive;
        pool.UpdatedUtc = now;
        BattlePassStore.SaveLotteryPools(pools);
        Audit("pool", "publish", pool.Id, $"发布奖池：{pool.Name}", poolId: pool.Id);
        return new { success = true, pool };
    }

    [HttpPost("pools/{poolId}/pause")]
    public object PausePool(string poolId, [FromBody] JsonElement request, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        return SetPoolStatus(poolId, BpLotteryConstants.PoolStatusPaused, "pause", request, token);
    }

    [HttpPost("pools/{poolId}/end")]
    public object EndPool(string poolId, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        return SetPoolStatus(poolId, BpLotteryConstants.PoolStatusEnded, "end", default, token);
    }

    [HttpPost("pools/{poolId}/archive")]
    public object ArchivePool(string poolId, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        return SetPoolStatus(poolId, BpLotteryConstants.PoolStatusArchived, "archive", default, token);
    }

    [HttpDelete("pools/{poolId}")]
    public object DeletePool(string poolId, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var pools = BattlePassStore.GetLotteryPools();
        var pool = pools.FirstOrDefault(p => string.Equals(p.Id, poolId, StringComparison.OrdinalIgnoreCase));
        if (pool is null)
        {
            return new { success = false, message = "奖池不存在" };
        }

        pools.Remove(pool);
        var deletedImages = DeletePoolLocalAssets(pool, pools);
        BattlePassStore.SaveLotteryPools(pools);
        var resetProgress = BattlePassStore.ResetLotteryPoolProgressForAll(poolId);
        Audit("pool", "delete", pool.Id, $"删除奖池：{pool.Name}；清理本地图片 {deletedImages} 个，进度 {resetProgress} 份", poolId: pool.Id);
        return new { success = true, deletedImages, resetProgress };
    }

    [HttpPost("assets/upload")]
    [RequestSizeLimit(MaxUploadBytes + 1024 * 1024)]
    public async Task<object> UploadAsset([FromQuery] string? kind = null, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        if (!Request.HasFormContentType)
        {
            return new { success = false, message = "请使用表单上传图片" };
        }

        var form = await Request.ReadFormAsync();
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null || file.Length <= 0)
        {
            return new { success = false, message = "未选择图片" };
        }

        if (file.Length > MaxUploadBytes)
        {
            return new { success = false, message = "图片不能超过 5 MB" };
        }

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedImageExtensions.Contains(ext))
        {
            return new { success = false, message = "仅支持 png / jpg / jpeg / webp / gif 图片" };
        }

        if (form.TryGetValue("kind", out var formKind) && !string.IsNullOrWhiteSpace(formKind))
        {
            kind = formKind.ToString();
        }

        var normalizedKind = string.Equals(kind, "cover", StringComparison.OrdinalIgnoreCase) ? "cover" : "icon";
        Directory.CreateDirectory(LotteryUploadDir);
        var fileName = $"{normalizedKind}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Guid.NewGuid().ToString("N")[..8]}{ext}";
        var targetPath = Path.Combine(LotteryUploadDir, fileName);

        await using (var stream = System.IO.File.Create(targetPath))
        {
            await file.CopyToAsync(stream);
        }

        var url = "/battlepass/uploads/lottery/" + fileName;
        Audit("pool", "upload-asset", fileName, $"上传奖池{(normalizedKind == "cover" ? "展示图" : "图标")}：{file.FileName}");
        return new { success = true, url, fileName, kind = normalizedKind };
    }

    [HttpPost("pools/{poolId}/copy-draft")]
    public object CopyPoolToDraft(string poolId, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var pools = BattlePassStore.GetLotteryPools();
        var pool = pools.FirstOrDefault(p => string.Equals(p.Id, poolId, StringComparison.OrdinalIgnoreCase));
        if (pool is null)
        {
            return new { success = false, message = "奖池不存在" };
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var copy = pool with
        {
            Id = "lottery_" + Guid.NewGuid().ToString("N")[..12],
            Name = pool.Name + " - 副本",
            Status = BpLotteryConstants.PoolStatusDraft,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        pools.Add(copy);
        BattlePassStore.SaveLotteryPools(pools);
        Audit("pool", "copy-draft", copy.Id, $"复制奖池为草稿：{pool.Name}", poolId: copy.Id);
        return new { success = true, pool = copy };
    }

    [HttpPost("pools/{poolId}/reset-progress")]
    public object ResetPoolProgress(string poolId, [FromBody] JsonElement request, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var all = request.ValueKind == JsonValueKind.Object
            && request.TryGetProperty("all", out var a)
            && a.GetBoolean();
        if (all)
        {
            var count = BattlePassStore.ResetLotteryPoolProgressForAll(poolId);
            Audit("progress", "reset-all", poolId, $"重置奖池所有玩家进度：{count}", poolId: poolId);
            return new { success = true, reset = count };
        }

        var profileId = request.ValueKind == JsonValueKind.Object && request.TryGetProperty("profileId", out var p)
            ? p.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return new { success = false, message = "缺少 profileId" };
        }

        var removed = BattlePassStore.ResetLotteryPoolProgress(profileId, poolId);
        Audit("progress", "reset-player", poolId, $"重置玩家奖池进度：{profileId}", profileId: profileId, poolId: poolId);
        return new { success = true, reset = removed ? 1 : 0 };
    }

    [HttpGet("records")]
    public object GetRecords([FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        return new { success = true, records = BattlePassStore.GetLotteryDrawRecords().OrderByDescending(r => r.CreatedUtc).ToList() };
    }

    [HttpGet("records/export")]
    public IActionResult ExportRecords([FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return Unauthorized();
        }

        var sb = new StringBuilder();
        sb.AppendLine("id,profileId,nickname,poolId,poolName,requestId,drawMode,drawIndex,prizeId,prizeName,prizeType,isGrandPrize,converted,exchangeCoinAmount,createdUtc");
        foreach (var r in BattlePassStore.GetLotteryDrawRecords().OrderByDescending(r => r.CreatedUtc))
        {
            sb.AppendLine(string.Join(",", [
                Csv(r.Id),
                Csv(r.ProfileId),
                Csv(r.NicknameSnapshot),
                Csv(r.PoolId),
                Csv(r.PoolNameSnapshot),
                Csv(r.RequestId),
                Csv(r.DrawMode),
                r.DrawIndexInRequest.ToString(),
                Csv(r.PrizeId),
                Csv(r.PrizeNameSnapshot),
                Csv(r.PrizeType),
                r.IsGrandPrize.ToString(),
                r.ConvertedToExchangeCoin.ToString(),
                r.ExchangeCoinAmount.ToString(),
                r.CreatedUtc.ToString(),
            ]));
        }

        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", "lottery-records.csv");
    }

    [HttpGet("players/search")]
    public object SearchPlayers([FromQuery] string? q = null, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        return new { success = true, players = battlePassService.SearchRealPlayers(q) };
    }

    [HttpPost("grants")]
    public object GrantCurrency([FromBody] JsonElement request, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var profileIds = ResolveGrantTargets(request);
        if (profileIds.Count == 0)
        {
            return new { success = false, message = "未指定玩家" };
        }

        var globalTickets = request.TryGetProperty("globalTickets", out var gt) ? gt.GetInt32() : 0;
        var poolId = request.TryGetProperty("poolId", out var pool) ? pool.GetString() : null;
        var poolTickets = request.TryGetProperty("poolTickets", out var pt) ? pt.GetInt32() : 0;
        var exchangeCoins = request.TryGetProperty("exchangeCoins", out var ec) ? ec.GetInt32() : 0;

        foreach (var profileId in profileIds)
        {
            walletService.Grant(profileId, globalTickets, poolId, poolTickets, exchangeCoins);
        }

        Audit("grant", "currency", null, $"发放抽奖资源给 {profileIds.Count} 个玩家");
        return new { success = true, count = profileIds.Count };
    }

    [HttpGet("shop")]
    public object GetShopItems([FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!CanRead(token))
        {
            return new { success = false, message = "未授权" };
        }

        return new { success = true, items = BattlePassStore.GetLotteryShopItems().OrderBy(i => i.SortOrder).ToList() };
    }

    [HttpPost("shop")]
    public object UpsertShopItem([FromBody] BpLotteryShopItem item, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        if (string.IsNullOrWhiteSpace(item.Id))
        {
            item.Id = "lottery_shop_" + Guid.NewGuid().ToString("N")[..12];
        }

        if (string.IsNullOrWhiteSpace(item.Status))
        {
            item.Status = BpLotteryConstants.PoolStatusDraft;
        }

        var items = BattlePassStore.GetLotteryShopItems();
        var existing = items.FirstOrDefault(i => string.Equals(i.Id, item.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            items.Add(item);
        }
        else
        {
            items[items.IndexOf(existing)] = item;
        }

        BattlePassStore.SaveLotteryShopItems(items);
        Audit("shop", existing is null ? "create" : "save", item.Id, $"保存兑换商品：{item.Name}");
        return new { success = true, item };
    }

    [HttpPost("shop/{itemId}/publish")]
    public object PublishShopItem(string itemId, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var items = BattlePassStore.GetLotteryShopItems();
        var item = items.FirstOrDefault(i => string.Equals(i.Id, itemId, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return new { success = false, message = "商品不存在" };
        }

        item.Status = BpLotteryConstants.PoolStatusActive;
        BattlePassStore.SaveLotteryShopItems(items);
        Audit("shop", "publish", item.Id, $"发布兑换商品：{item.Name}");
        return new { success = true, item };
    }

    [HttpGet("transactions")]
    public object Transactions([FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        return new { success = true, transactions = BattlePassStore.GetLotteryTransactions().OrderByDescending(t => t.CreatedUtc).ToList() };
    }

    [HttpPost("transactions/{transactionId}/mark-handled")]
    public object MarkTransactionHandled(string transactionId, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var transactions = BattlePassStore.GetLotteryTransactions();
        var tx = transactions.FirstOrDefault(t => string.Equals(t.Id, transactionId, StringComparison.OrdinalIgnoreCase));
        if (tx is null)
        {
            return new { success = false, message = "事务不存在" };
        }

        tx.Status = BpLotteryConstants.TransactionFailed;
        tx.Message = (tx.Message ?? "") + "；管理员已标记处理";
        tx.UpdatedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        BattlePassStore.SaveLotteryTransactions(transactions);
        Audit("transaction", "mark-handled", transactionId, "标记异常事务已处理");
        return new { success = true, transaction = tx };
    }

    [HttpGet("audit-logs")]
    public object AuditLogs([FromQuery] string? category = null, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var logs = BattlePassStore.GetLotteryAuditLogs().AsEnumerable();
        if (!string.IsNullOrWhiteSpace(category))
        {
            logs = logs.Where(l => string.Equals(l.Category, category, StringComparison.OrdinalIgnoreCase));
        }

        return new { success = true, logs = logs.OrderByDescending(l => l.CreatedUtc).ToList() };
    }

    private object SetPoolStatus(string poolId, string status, string action, JsonElement request, string? token)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var pools = BattlePassStore.GetLotteryPools();
        var pool = pools.FirstOrDefault(p => string.Equals(p.Id, poolId, StringComparison.OrdinalIgnoreCase));
        if (pool is null)
        {
            return new { success = false, message = "奖池不存在" };
        }

        if (string.Equals(status, BpLotteryConstants.PoolStatusPaused, StringComparison.OrdinalIgnoreCase)
            && request.ValueKind == JsonValueKind.Object
            && request.TryGetProperty("visible", out var visible))
        {
            pool.PauseVisibleToPlayers = visible.GetBoolean();
        }

        pool.Status = status;
        pool.UpdatedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        BattlePassStore.SaveLotteryPools(pools);
        Audit("pool", action, pool.Id, $"设置奖池状态：{pool.Name} -> {status}", poolId: pool.Id);
        return new { success = true, pool };
    }

    private static int DeletePoolLocalAssets(BpLotteryPool pool, List<BpLotteryPool> remainingPools)
    {
        var deleted = 0;
        deleted += DeleteLocalAssetIfUnreferenced(pool.IconUrl, remainingPools);
        deleted += DeleteLocalAssetIfUnreferenced(pool.CoverUrl, remainingPools);
        return deleted;
    }

    private static int DeleteLocalAssetIfUnreferenced(string? url, List<BpLotteryPool> remainingPools)
    {
        if (!TryResolveLocalLotteryAsset(url, out var path))
        {
            return 0;
        }

        var stillReferenced = remainingPools.Any(pool =>
            IsSameLocalAsset(pool.IconUrl, path) || IsSameLocalAsset(pool.CoverUrl, path));
        if (stillReferenced || !System.IO.File.Exists(path))
        {
            return 0;
        }

        try
        {
            System.IO.File.Delete(path);
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsSameLocalAsset(string? url, string path)
    {
        return TryResolveLocalLotteryAsset(url, out var other)
            && string.Equals(other, path, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveLocalLotteryAsset(string? url, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var value = url.Trim().Replace('\\', '/');
        var queryIndex = value.IndexOfAny(new[] { '?', '#' });
        if (queryIndex >= 0)
        {
            value = value[..queryIndex];
        }

        const string publicPrefix = "/battlepass/uploads/lottery/";
        const string relativePrefix = "uploads/lottery/";
        string? fileName = null;
        if (value.StartsWith(publicPrefix, StringComparison.OrdinalIgnoreCase))
        {
            fileName = value[publicPrefix.Length..];
        }
        else if (value.StartsWith(relativePrefix, StringComparison.OrdinalIgnoreCase))
        {
            fileName = value[relativePrefix.Length..];
        }

        if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains('/'))
        {
            return false;
        }

        fileName = Path.GetFileName(fileName);
        var baseDir = Path.GetFullPath(LotteryUploadDir);
        var full = Path.GetFullPath(Path.Combine(baseDir, fileName));
        var prefix = baseDir.EndsWith(Path.DirectorySeparatorChar)
            ? baseDir
            : baseDir + Path.DirectorySeparatorChar;
        if (!(full + Path.DirectorySeparatorChar).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        path = full;
        return true;
    }

    private static List<string> ResolveGrantTargets(JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        if (request.TryGetProperty("all", out var all) && all.ValueKind == JsonValueKind.True)
        {
            return BattlePassStore
                .ListProgressProfileIds()
                .Union(BattlePassStore.ListLotteryWalletProfileIds(), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var result = new List<string>();
        if (request.TryGetProperty("profileId", out var profileId))
        {
            var value = profileId.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                result.Add(value);
            }
        }

        if (request.TryGetProperty("profileIds", out var profileIds) && profileIds.ValueKind == JsonValueKind.Array)
        {
            result.AddRange(profileIds.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x))!);
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList()!;
    }

    private static void Audit(
        string category,
        string action,
        string? targetId,
        string summary,
        string? profileId = null,
        string? poolId = null
    )
    {
        BattlePassStore.AppendLotteryAuditLog(new BpLotteryAuditLog
        {
            Id = Guid.NewGuid().ToString("N"),
            Category = category,
            Action = action,
            TargetId = targetId,
            Summary = summary,
            ProfileId = profileId,
            PoolId = poolId,
            CreatedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
    }

    private static string Csv(string? value)
    {
        value ??= "";
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
