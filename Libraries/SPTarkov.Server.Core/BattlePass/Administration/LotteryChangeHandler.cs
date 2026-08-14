using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace SPTarkov.Server.Core.BattlePass.Administration;

/// <summary>抽奖模块命令处理器：协管可提交 settings/pool/shopItem 草稿的增删改。</summary>
[Injectable(InjectionType.Singleton)]
public class LotteryChangeHandler(ISptLogger<LotteryChangeHandler> logger) : IBattlePassChangeHandler
{
    public string Module => "lottery";

    public IReadOnlyList<string> CommandTypes { get; } =
        ["lottery.settings", "lottery.pool.upsert", "lottery.pool.delete", "lottery.shop.upsert", "lottery.shop.delete"];

    public string RequiredCapability => "lottery.submit";

    public object Normalize(string commandType, JsonElement input)
    {
        return commandType switch
        {
            "lottery.settings" => JsonSerializer.Deserialize<BpLotterySettings>(input.GetRawText())
                ?? throw new ArgumentException("无法反序列化抽奖设置"),
            "lottery.pool.upsert" => NormalizePool(input),
            "lottery.pool.delete" => ExtractId(input),
            "lottery.shop.upsert" => NormalizeShopItem(input),
            "lottery.shop.delete" => ExtractId(input),
            _ => throw new ArgumentException($"Unknown commandType: {commandType}"),
        };
    }

    public string? Validate(string commandType, object normalizedInput)
    {
        return commandType switch
        {
            "lottery.pool.upsert" => ValidatePool((BpLotteryPool) normalizedInput),
            "lottery.shop.upsert" => ValidateShopItem((BpLotteryShopItem) normalizedInput),
            "lottery.pool.delete" or "lottery.shop.delete" =>
                string.IsNullOrWhiteSpace((string) normalizedInput) ? "缺少 id" : null,
            _ => null,
        };
    }

    public string Describe(string commandType, object normalizedInput, object? currentState)
    {
        return commandType switch
        {
            "lottery.settings" => "修改抽奖全局设置",
            "lottery.pool.upsert" => currentState is null
                ? $"新增奖池草稿: {((BpLotteryPool) normalizedInput).Name}"
                : $"编辑奖池草稿: {((BpLotteryPool) normalizedInput).Name}",
            "lottery.pool.delete" => $"删除奖池草稿: {(string) normalizedInput}",
            "lottery.shop.upsert" => currentState is null
                ? $"新增兑换商品草稿: {((BpLotteryShopItem) normalizedInput).Name}"
                : $"编辑兑换商品草稿: {((BpLotteryShopItem) normalizedInput).Name}",
            "lottery.shop.delete" => $"删除兑换商品草稿: {(string) normalizedInput}",
            _ => commandType,
        };
    }

    public string GetTargetKey(string commandType, object normalizedInput)
    {
        return commandType switch
        {
            "lottery.settings" => "lottery:settings:_global",
            "lottery.pool.upsert" => $"lottery:pool:{((BpLotteryPool) normalizedInput).Id}",
            "lottery.pool.delete" => $"lottery:pool:{(string) normalizedInput}",
            "lottery.shop.upsert" => $"lottery:shopItem:{((BpLotteryShopItem) normalizedInput).Id}",
            "lottery.shop.delete" => $"lottery:shopItem:{(string) normalizedInput}",
            _ => $"lottery:unknown:{commandType}",
        };
    }

    public string GetTargetDisplayName(string commandType, object normalizedInput)
    {
        return commandType switch
        {
            "lottery.settings" => "抽奖全局设置",
            "lottery.pool.upsert" => ((BpLotteryPool) normalizedInput).Name ?? "(未命名)",
            "lottery.pool.delete" => (string) normalizedInput,
            "lottery.shop.upsert" => ((BpLotteryShopItem) normalizedInput).Name ?? "(未命名)",
            "lottery.shop.delete" => (string) normalizedInput,
            _ => commandType,
        };
    }

    public object? GetCurrentSnapshot(string targetKey)
    {
        var parts = targetKey.Split(':', 3);
        if (parts.Length < 3) return null;

        return parts[1] switch
        {
            "settings" => BattlePassStore.GetLotterySettings(),
            "pool" => BattlePassStore.GetLotteryPools()
                .FirstOrDefault(p => string.Equals(p.Id, parts[2], StringComparison.OrdinalIgnoreCase)),
            "shopItem" => BattlePassStore.GetLotteryShopItems()
                .FirstOrDefault(i => string.Equals(i.Id, parts[2], StringComparison.OrdinalIgnoreCase)),
            _ => null,
        };
    }

    public string GetRevision(object? snapshot)
    {
        if (snapshot is null) return "null";
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexStringLower(hash);
    }

    public string ApplyAndActivate(string commandType, object normalizedInput, string? expectedBaseRevision, string? changeId)
    {
        switch (commandType)
        {
            case "lottery.settings":
                BattlePassStore.SaveLotterySettings((BpLotterySettings) normalizedInput);
                return GetRevision(GetCurrentSnapshot("lottery:settings:_global"));

            case "lottery.pool.upsert":
                return ApplyPoolUpsert((BpLotteryPool) normalizedInput);

            case "lottery.pool.delete":
                return ApplyPoolDelete((string) normalizedInput);

            case "lottery.shop.upsert":
                return ApplyShopItemUpsert((BpLotteryShopItem) normalizedInput);

            case "lottery.shop.delete":
                return ApplyShopItemDelete((string) normalizedInput);

            default:
                throw new InvalidOperationException($"不支持的命令类型: {commandType}");
        }
    }

    public string RestoreSnapshot(string targetKey, object? beforeSnapshot, string expectedCurrentRevision, string? auditId)
    {
        var currentRevision = GetRevision(GetCurrentSnapshot(targetKey));
        if (!string.Equals(currentRevision, expectedCurrentRevision, StringComparison.Ordinal))
            throw new ChangeConflictException($"回溯目标 {targetKey} 已发生后续改动: expected={expectedCurrentRevision}, actual={currentRevision}");

        var parts = targetKey.Split(':', 3);
        if (parts.Length < 3) throw new InvalidOperationException($"无效目标键: {targetKey}");
        switch (parts[1])
        {
            case "settings":
                BattlePassStore.SaveLotterySettings(BattlePassSnapshotCodec.Deserialize<BpLotterySettings>(beforeSnapshot));
                break;
            case "pool":
                var pools = BattlePassStore.GetLotteryPools();
                pools.RemoveAll(pool => string.Equals(pool.Id, parts[2], StringComparison.OrdinalIgnoreCase));
                if (beforeSnapshot is not null) pools.Add(BattlePassSnapshotCodec.Deserialize<BpLotteryPool>(beforeSnapshot));
                BattlePassStore.SaveLotteryPools(pools);
                break;
            case "shopItem":
                var items = BattlePassStore.GetLotteryShopItems();
                items.RemoveAll(item => string.Equals(item.Id, parts[2], StringComparison.OrdinalIgnoreCase));
                if (beforeSnapshot is not null) items.Add(BattlePassSnapshotCodec.Deserialize<BpLotteryShopItem>(beforeSnapshot));
                BattlePassStore.SaveLotteryShopItems(items);
                break;
            default:
                throw new InvalidOperationException($"不支持回溯目标: {targetKey}");
        }

        return GetRevision(GetCurrentSnapshot(targetKey));
    }

    // ---- Private ----

    private static BpLotteryPool NormalizePool(JsonElement input)
    {
        var pool = JsonSerializer.Deserialize<BpLotteryPool>(input.GetRawText())
            ?? throw new ArgumentException("无法反序列化奖池");
        if (string.IsNullOrWhiteSpace(pool.Id))
            pool.Id = "lottery_" + Guid.NewGuid().ToString("N")[..12];
        if (string.IsNullOrWhiteSpace(pool.Status))
            pool.Status = BpLotteryConstants.PoolStatusDraft;
        return pool;
    }

    private static BpLotteryShopItem NormalizeShopItem(JsonElement input)
    {
        var item = JsonSerializer.Deserialize<BpLotteryShopItem>(input.GetRawText())
            ?? throw new ArgumentException("无法反序列化兑换商品");
        if (string.IsNullOrWhiteSpace(item.Id))
            item.Id = "lottery_shop_" + Guid.NewGuid().ToString("N")[..12];
        if (string.IsNullOrWhiteSpace(item.Status))
            item.Status = BpLotteryConstants.PoolStatusDraft;
        return item;
    }

    private static string ExtractId(JsonElement input)
    {
        var id = input.TryGetProperty("id", out var p) ? p.GetString()?.Trim() : null;
        return id ?? throw new ArgumentException("缺少 id");
    }

    private static string? ValidatePool(BpLotteryPool pool)
    {
        if (string.IsNullOrWhiteSpace(pool.Name)) return "奖池名称不能为空";
        return null;
    }

    private static string? ValidateShopItem(BpLotteryShopItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Name)) return "商品名称不能为空";
        return null;
    }

    private string ApplyPoolUpsert(BpLotteryPool pool)
    {
        var pools = BattlePassStore.GetLotteryPools();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var existing = pools.FirstOrDefault(p => string.Equals(p.Id, pool.Id, StringComparison.OrdinalIgnoreCase));
        pool.CreatedUtc = existing?.CreatedUtc > 0 ? existing.CreatedUtc : now;
        pool.UpdatedUtc = now;

        if (existing is null) pools.Add(pool);
        else pools[pools.IndexOf(existing)] = pool;

        BattlePassStore.SaveLotteryPools(pools);
        return GetRevision(GetCurrentSnapshot($"lottery:pool:{pool.Id}"));
    }

    private string ApplyPoolDelete(string id)
    {
        var pools = BattlePassStore.GetLotteryPools();
        pools.RemoveAll(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        BattlePassStore.SaveLotteryPools(pools);
        return GetRevision(null);
    }

    private string ApplyShopItemUpsert(BpLotteryShopItem item)
    {
        var items = BattlePassStore.GetLotteryShopItems();
        var existing = items.FirstOrDefault(i => string.Equals(i.Id, item.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is null) items.Add(item);
        else items[items.IndexOf(existing)] = item;
        BattlePassStore.SaveLotteryShopItems(items);
        return GetRevision(GetCurrentSnapshot($"lottery:shopItem:{item.Id}"));
    }

    private string ApplyShopItemDelete(string id)
    {
        var items = BattlePassStore.GetLotteryShopItems();
        items.RemoveAll(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
        BattlePassStore.SaveLotteryShopItems(items);
        return GetRevision(null);
    }
}
