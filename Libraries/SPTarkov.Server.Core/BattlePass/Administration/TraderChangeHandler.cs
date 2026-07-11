using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;

namespace SPTarkov.Server.Core.BattlePass.Administration;

/// <summary>
///     通行证商人模块命令处理器：货架商品新增/编辑/删除（trader.json）。
///     <para>商品为实体物品（tpl 必填、以物易物计价），无虚拟奖励类型；应用后调用
///     <see cref="BattlePassTraderSync.Sync"/> 热重注入。商人元信息/头像属商人级配置与文件上传，
///     不走审核（仅管理员），故不在此处理。</para>
/// </summary>
[Injectable]
public class TraderChangeHandler(
    BattlePassTraderSync traderSync,
    ISptLogger<TraderChangeHandler> logger
) : IBattlePassChangeHandler
{
    private static readonly JsonSerializerOptions SerializeOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string Module => "trader";
    public IReadOnlyList<string> CommandTypes { get; } = ["trader.offer.upsert", "trader.offer.delete"];
    public string RequiredCapability => "trader.submit";

    public object Normalize(string commandType, JsonElement input)
    {
        return commandType switch
        {
            "trader.offer.upsert" => NormalizeUpsert(input),
            "trader.offer.delete" => NormalizeDelete(input),
            _ => throw new ArgumentException($"未知命令类型: {commandType}"),
        };
    }

    public string? Validate(string commandType, object normalizedInput)
    {
        return commandType switch
        {
            "trader.offer.upsert" => ValidateUpsert((BpTraderOffer) normalizedInput),
            "trader.offer.delete" => ValidateDelete((TraderOfferDeleteInput) normalizedInput),
            _ => $"未知命令类型: {commandType}",
        };
    }

    public string Describe(string commandType, object normalizedInput, object? currentState)
    {
        switch (commandType)
        {
            case "trader.offer.upsert":
                var offer = (BpTraderOffer) normalizedInput;
                var verb = currentState is null ? "新增" : "编辑";
                var name = !string.IsNullOrWhiteSpace(offer.Name) ? offer.Name : offer.Tpl;
                return $"{verb}商人货架 {name}（id={offer.Id}）";
            case "trader.offer.delete":
                var del = (TraderOfferDeleteInput) normalizedInput;
                return $"删除商人货架 id={del.Id}";
            default:
                return commandType;
        }
    }

    public string GetTargetKey(string commandType, object normalizedInput)
    {
        return commandType switch
        {
            "trader.offer.upsert" => $"trader:offer:{((BpTraderOffer) normalizedInput).Id}",
            "trader.offer.delete" => $"trader:offer:{((TraderOfferDeleteInput) normalizedInput).Id}",
            _ => $"trader:unknown:{commandType}",
        };
    }

    public string GetTargetDisplayName(string commandType, object normalizedInput)
    {
        return commandType switch
        {
            "trader.offer.upsert" => ((BpTraderOffer) normalizedInput).Name ?? ((BpTraderOffer) normalizedInput).Id,
            "trader.offer.delete" => ((TraderOfferDeleteInput) normalizedInput).Id,
            _ => commandType,
        };
    }

    public object? GetCurrentSnapshot(string targetKey)
    {
        // targetKey format: trader:offer:<id>
        var parts = targetKey.Split(':', 3);
        if (parts.Length < 3 || parts[1] != "offer") return null;

        var offerId = parts[2];
        return BattlePassStore.GetOffers()
            .FirstOrDefault(o => string.Equals(o.Id, offerId, StringComparison.OrdinalIgnoreCase));
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

        return commandType switch
        {
            "trader.offer.upsert" => ApplyUpsert((BpTraderOffer) normalizedInput),
            "trader.offer.delete" => ApplyDelete((TraderOfferDeleteInput) normalizedInput),
            _ => throw new ArgumentException($"未知命令类型: {commandType}"),
        };
    }

    public string RestoreSnapshot(string targetKey, object? beforeSnapshot, string expectedCurrentRevision, string? auditId)
    {
        var currentRevision = GetRevision(GetCurrentSnapshot(targetKey));
        if (!string.Equals(currentRevision, expectedCurrentRevision, StringComparison.Ordinal))
            throw new ChangeConflictException($"回溯目标 {targetKey} 已发生后续改动：期望 {expectedCurrentRevision}，当前 {currentRevision}");

        var parts = targetKey.Split(':', 3);
        if (parts.Length < 3 || parts[1] != "offer") throw new InvalidOperationException($"不支持回溯目标: {targetKey}");
        return beforeSnapshot is null
            ? ApplyDelete(new TraderOfferDeleteInput { Id = parts[2] })
            : ApplyUpsert(BattlePassSnapshotCodec.Deserialize<BpTraderOffer>(beforeSnapshot));
    }

    // ---- Normalize helpers ----

    private static BpTraderOffer NormalizeUpsert(JsonElement input)
    {
        var offer = JsonSerializer.Deserialize<BpTraderOffer>(input.GetRawText(), SerializeOpts)
            ?? throw new ArgumentException("无法解析货架项数据");

        offer.Id = (offer.Id ?? "").Trim();
        offer.Tpl = (offer.Tpl ?? "").Trim();
        offer.Cost ??= new List<BpBarterCost>();
        foreach (var cost in offer.Cost)
        {
            cost.Tpl = (cost.Tpl ?? "").Trim();
        }

        offer.SellCount = Math.Max(1, offer.SellCount);
        offer.BuyLimit = Math.Max(0, offer.BuyLimit);

        return offer;
    }

    private static TraderOfferDeleteInput NormalizeDelete(JsonElement input)
    {
        var id = input.TryGetProperty("id", out var prop) ? prop.GetString()?.Trim() : null;
        return new TraderOfferDeleteInput { Id = id ?? "" };
    }

    // ---- Validate helpers ----

    private static string? ValidateUpsert(BpTraderOffer offer)
    {
        if (string.IsNullOrWhiteSpace(offer.Id))
        {
            return "货架项 id 不能为空";
        }

        if (!MongoId.IsValidMongoId(offer.Tpl))
        {
            return "商品 tpl 无效";
        }

        if (offer.Cost.Any(c => c is null || !MongoId.IsValidMongoId(c.Tpl?.Trim()) || c.Count <= 0))
        {
            return "支付物品 tpl 无效或数量不是正整数";
        }

        return null;
    }

    private static string? ValidateDelete(TraderOfferDeleteInput input)
    {
        return string.IsNullOrWhiteSpace(input.Id) ? "缺少 id" : null;
    }

    // ---- Apply helpers ----

    private string ApplyUpsert(BpTraderOffer offer)
    {
        var offers = BattlePassStore.GetOffers();
        var existing = offers.FirstOrDefault(o => string.Equals(o.Id, offer.Id, StringComparison.OrdinalIgnoreCase));

        // 库存变化时清销量（与管理员即时写入端点一致，键前缀 trader:）
        if (existing is not null && existing.Stock != offer.Stock)
        {
            ResetTraderSales(offer.Id);
        }

        offers.RemoveAll(o => string.Equals(o.Id, offer.Id, StringComparison.OrdinalIgnoreCase));
        offers.Add(offer);
        BattlePassStore.SaveOffers(offers);
        traderSync.Sync(); // 热重注入（客户端商人界面可能需重进/重登刷新）

        var saved = BattlePassStore.GetOffers()
            .FirstOrDefault(o => string.Equals(o.Id, offer.Id, StringComparison.OrdinalIgnoreCase));
        return GetRevision(saved);
    }

    private string ApplyDelete(TraderOfferDeleteInput input)
    {
        var offers = BattlePassStore.GetOffers();
        var removed = offers.RemoveAll(o => string.Equals(o.Id, input.Id, StringComparison.OrdinalIgnoreCase));
        BattlePassStore.SaveOffers(offers);
        if (removed > 0)
        {
            ResetTraderSales(input.Id);
        }

        traderSync.Sync();
        return GetRevision(null); // 删除后目标不存在，revision 为空
    }

    // ---- Shared helpers ----

    private static void ResetTraderSales(string offerId)
    {
        var offerKey = $"trader:{offerId}";
        var state = BattlePassStore.GetShopState();
        var salesRemoved = state.Sales.Remove(offerKey);
        var periodRemoved = state.OfferPeriods.Remove(offerKey);
        if (salesRemoved || periodRemoved)
        {
            BattlePassStore.SaveShopState(state);
        }
    }
}

/// <summary>商人货架项删除命令的规范化输入。</summary>
public record TraderOfferDeleteInput
{
    public string Id { get; set; } = "";
}
