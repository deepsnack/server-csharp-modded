using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;

namespace SPTarkov.Server.Core.BattlePass.Administration;

/// <summary>商店模块命令处理器：商品新增/编辑/删除、刷新周期修改。</summary>
[Injectable]
public class ShopChangeHandler(
    ISptLogger<ShopChangeHandler> logger
) : IBattlePassChangeHandler
{
    private static readonly JsonSerializerOptions SerializeOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string Module => "shop";
    public IReadOnlyList<string> CommandTypes { get; } = ["shop.upsert", "shop.delete", "shop.refreshPeriod"];
    public string RequiredCapability => "shop.submit";

    public object Normalize(string commandType, JsonElement input)
    {
        return commandType switch
        {
            "shop.upsert" => NormalizeUpsert(input),
            "shop.delete" => NormalizeDelete(input),
            "shop.refreshPeriod" => NormalizeRefreshPeriod(input),
            _ => throw new ArgumentException($"未知命令类型: {commandType}"),
        };
    }

    public string? Validate(string commandType, object normalizedInput)
    {
        return commandType switch
        {
            "shop.upsert" => ValidateUpsert((BpTraderOffer) normalizedInput),
            "shop.delete" => ValidateDelete((ShopDeleteInput) normalizedInput),
            "shop.refreshPeriod" => null, // NormalizeRefreshPeriod 已夹值，无需额外校验
            _ => $"未知命令类型: {commandType}",
        };
    }

    public string Describe(string commandType, object normalizedInput, object? currentState)
    {
        switch (commandType)
        {
            case "shop.upsert":
                var offer = (BpTraderOffer) normalizedInput;
                var verb = currentState is null ? "新增" : "编辑";
                var name = !string.IsNullOrWhiteSpace(offer.Name) ? offer.Name : offer.Tpl;
                return $"{verb}商品 {name}（id={offer.Id}）";
            case "shop.delete":
                var del = (ShopDeleteInput) normalizedInput;
                return $"删除商品 id={del.Id}";
            case "shop.refreshPeriod":
                var rp = (ShopRefreshPeriodInput) normalizedInput;
                return rp.Seconds > 0 ? $"设置刷新周期为 {rp.Seconds} 秒" : "关闭刷新周期";
            default:
                return commandType;
        }
    }

    public string GetTargetKey(string commandType, object normalizedInput)
    {
        return commandType switch
        {
            "shop.upsert" => $"shop:offer:{((BpTraderOffer) normalizedInput).Id}",
            "shop.delete" => $"shop:offer:{((ShopDeleteInput) normalizedInput).Id}",
            "shop.refreshPeriod" => "shop:config:refreshPeriod",
            _ => $"shop:unknown:{commandType}",
        };
    }

    public string GetTargetDisplayName(string commandType, object normalizedInput)
    {
        return commandType switch
        {
            "shop.upsert" => ((BpTraderOffer) normalizedInput).Name ?? ((BpTraderOffer) normalizedInput).Id,
            "shop.delete" => ((ShopDeleteInput) normalizedInput).Id,
            "shop.refreshPeriod" => "商店刷新周期",
            _ => commandType,
        };
    }

    public object? GetCurrentSnapshot(string targetKey)
    {
        // targetKey format: shop:offer:<id> or shop:config:refreshPeriod
        var parts = targetKey.Split(':', 3);
        if (parts.Length < 3) return null;

        if (parts[1] == "offer")
        {
            var offerId = parts[2];
            return BattlePassStore.GetShopOffers()
                .FirstOrDefault(o => string.Equals(o.Id, offerId, StringComparison.OrdinalIgnoreCase));
        }

        if (parts[1] == "config" && parts[2] == "refreshPeriod")
        {
            var state = BattlePassStore.GetShopState();
            return new ShopRefreshPeriodInput { Seconds = state.RefreshSeconds };
        }

        return null;
    }

    public string GetRevision(object? snapshot)
    {
        if (snapshot is null) return "";
        var json = JsonSerializer.Serialize(snapshot, snapshot.GetType(), SerializeOpts);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    public string ApplyAndActivate(string commandType, object normalizedInput, string? expectedBaseRevision, string? changeId)
    {
        var targetKey = GetTargetKey(commandType, normalizedInput);

        // 如果有预期基线，校验当前版本
        if (!string.IsNullOrEmpty(expectedBaseRevision))
        {
            var currentSnapshot = GetCurrentSnapshot(targetKey);
            var currentRevision = GetRevision(currentSnapshot);
            if (!string.Equals(currentRevision, expectedBaseRevision, StringComparison.Ordinal))
            {
                throw new ChangeConflictException(
                    $"目标 {targetKey} 基线已变化：期望 {expectedBaseRevision}，当前 {currentRevision}");
            }
        }

        switch (commandType)
        {
            case "shop.upsert":
                return ApplyUpsert((BpTraderOffer) normalizedInput);
            case "shop.delete":
                return ApplyDelete((ShopDeleteInput) normalizedInput);
            case "shop.refreshPeriod":
                return ApplyRefreshPeriod((ShopRefreshPeriodInput) normalizedInput);
            default:
                throw new ArgumentException($"未知命令类型: {commandType}");
        }
    }

    public string RestoreSnapshot(string targetKey, object? beforeSnapshot, string expectedCurrentRevision, string? auditId)
    {
        EnsureRestoreRevision(targetKey, expectedCurrentRevision);
        var parts = targetKey.Split(':', 3);
        if (parts.Length < 3) throw new InvalidOperationException($"无效目标键: {targetKey}");

        if (parts[1] == "offer")
        {
            return beforeSnapshot is null
                ? ApplyDelete(new ShopDeleteInput { Id = parts[2] })
                : ApplyUpsert(BattlePassSnapshotCodec.Deserialize<BpTraderOffer>(beforeSnapshot));
        }

        if (parts[1] == "config" && parts[2] == "refreshPeriod")
        {
            return ApplyRefreshPeriod(BattlePassSnapshotCodec.Deserialize<ShopRefreshPeriodInput>(beforeSnapshot));
        }

        throw new InvalidOperationException($"不支持回溯目标: {targetKey}");
    }

    private void EnsureRestoreRevision(string targetKey, string expectedCurrentRevision)
    {
        var currentRevision = GetRevision(GetCurrentSnapshot(targetKey));
        if (!string.Equals(currentRevision, expectedCurrentRevision, StringComparison.Ordinal))
        {
            throw new ChangeConflictException($"回溯目标 {targetKey} 已发生后续改动：期望 {expectedCurrentRevision}，当前 {currentRevision}");
        }
    }

    // ---- Normalize helpers ----

    private static BpTraderOffer NormalizeUpsert(JsonElement input)
    {
        var offer = JsonSerializer.Deserialize<BpTraderOffer>(input.GetRawText(), SerializeOpts)
            ?? throw new ArgumentException("无法解析商品数据");

        offer.Id = (offer.Id ?? "").Trim();
        offer.RewardType = NormalizeRewardType(offer.RewardType);
        var isVirtual = BattlePassShopService.IsVirtualRewardType(offer.RewardType);

        if (isVirtual)
        {
            offer.Tpl = "";
            if (string.Equals(offer.RewardType, "lotteryPoolTickets", StringComparison.OrdinalIgnoreCase))
            {
                offer.PoolId = offer.PoolId?.Trim();
            }
            else
            {
                offer.PoolId = null;
            }
        }
        else
        {
            offer.Tpl = (offer.Tpl ?? "").Trim();
            offer.PoolId = null;
        }

        offer.Cost ??= new List<BpBarterCost>();
        foreach (var cost in offer.Cost)
        {
            cost.Tpl = (cost.Tpl ?? "").Trim();
        }

        offer.SellCount = Math.Max(1, offer.SellCount);
        offer.BuyLimit = Math.Max(0, offer.BuyLimit);
        if (offer.RefreshSeconds.HasValue)
        {
            offer.RefreshSeconds = Math.Max(0, offer.RefreshSeconds.Value);
        }

        return offer;
    }

    private static ShopDeleteInput NormalizeDelete(JsonElement input)
    {
        var id = input.TryGetProperty("id", out var prop) ? prop.GetString()?.Trim() : null;
        return new ShopDeleteInput { Id = id ?? "" };
    }

    private static ShopRefreshPeriodInput NormalizeRefreshPeriod(JsonElement input)
    {
        var seconds = input.TryGetProperty("seconds", out var s) && s.TryGetInt32(out var v) ? v : 0;
        return new ShopRefreshPeriodInput { Seconds = Math.Max(0, seconds) };
    }

    // ---- Validate helpers ----

    private static string? ValidateUpsert(BpTraderOffer offer)
    {
        if (string.IsNullOrWhiteSpace(offer.Id))
        {
            return "货架项 id 不能为空";
        }

        var isVirtual = BattlePassShopService.IsVirtualRewardType(offer.RewardType);

        if (isVirtual)
        {
            if (string.Equals(offer.RewardType, "lotteryPoolTickets", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(offer.PoolId))
                {
                    return "限定抽奖券必须选择绑定奖池";
                }

                var exists = BattlePassStore.GetLotteryPools()
                    .Any(pool => string.Equals(pool.Id, offer.PoolId, StringComparison.OrdinalIgnoreCase));
                if (!exists)
                {
                    return "绑定奖池不存在";
                }
            }
        }
        else
        {
            if (!MongoId.IsValidMongoId(offer.Tpl))
            {
                return "商品 tpl 无效";
            }
        }

        if (offer.Cost.Any(c => c is null || !MongoId.IsValidMongoId(c.Tpl?.Trim()) || c.Count <= 0))
        {
            return "支付物品 tpl 无效或数量不是正整数";
        }

        return null;
    }

    private static string? ValidateDelete(ShopDeleteInput input)
    {
        return string.IsNullOrWhiteSpace(input.Id) ? "缺少 id" : null;
    }

    // ---- Apply helpers ----

    private string ApplyUpsert(BpTraderOffer offer)
    {
        var offers = BattlePassStore.GetShopOffers();
        var existing = offers.FirstOrDefault(o => string.Equals(o.Id, offer.Id, StringComparison.OrdinalIgnoreCase));

        // 库存或刷新周期变化时清销量
        if (existing is not null && (existing.Stock != offer.Stock || existing.RefreshSeconds != offer.RefreshSeconds))
        {
            ResetShopSales($"custom:{offer.Id}");
        }

        offers.RemoveAll(o => string.Equals(o.Id, offer.Id, StringComparison.OrdinalIgnoreCase));
        offers.Add(offer);
        BattlePassStore.SaveShopOffers(offers);

        // 确认写入后重新读取计算 revision
        var saved = BattlePassStore.GetShopOffers()
            .FirstOrDefault(o => string.Equals(o.Id, offer.Id, StringComparison.OrdinalIgnoreCase));
        return GetRevision(saved);
    }

    private string ApplyDelete(ShopDeleteInput input)
    {
        var offers = BattlePassStore.GetShopOffers();
        var removed = offers.RemoveAll(o => string.Equals(o.Id, input.Id, StringComparison.OrdinalIgnoreCase));
        BattlePassStore.SaveShopOffers(offers);
        if (removed > 0)
        {
            ResetShopSales($"custom:{input.Id}");
        }

        return GetRevision(null); // 删除后目标不存在，revision 为空
    }

    private string ApplyRefreshPeriod(ShopRefreshPeriodInput input)
    {
        var state = BattlePassStore.GetShopState();
        state.RefreshSeconds = input.Seconds;
        state.PeriodStartUtc = input.Seconds > 0 ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : 0;
        BattlePassStore.SaveShopState(state);

        var updated = BattlePassStore.GetShopState();
        return GetRevision(new ShopRefreshPeriodInput { Seconds = updated.RefreshSeconds });
    }

    // ---- Shared helpers ----

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

    private static string NormalizeRewardType(string? rewardType)
    {
        return (rewardType ?? "item").Trim().ToLowerInvariant() switch
        {
            "lotteryglobaltickets" => "lotteryGlobalTickets",
            "lotterypooltickets" => "lotteryPoolTickets",
            "lotteryexchangecoins" => "lotteryExchangeCoins",
            _ => "item",
        };
    }
}

/// <summary>商品删除命令的规范化输入。</summary>
public record ShopDeleteInput
{
    public string Id { get; set; } = "";
}

/// <summary>刷新周期修改命令的规范化输入。</summary>
public record ShopRefreshPeriodInput
{
    public int Seconds { get; set; }
}
