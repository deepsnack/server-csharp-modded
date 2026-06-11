using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Services;

namespace SPTarkov.Server.Core.BattlePass.ItemControl;

/// <summary>物品检索：按名称/tpl 查 <c>Templates.Items</c>，可按 原版/mod 过滤。供物品页与跳蚤页共用。</summary>
[Injectable]
public class ItemSearchService(
    DatabaseService databaseService,
    LocaleService localeService,
    ItemBaselineService baseline
)
{
    /// <summary>检索。<paramref name="source"/> = all | mod | vanilla。返回至多 <paramref name="limit"/> 条。</summary>
    public List<BpItemSearchHit> Search(string? query, string? source, int limit = 60)
    {
        var q = (query ?? "").Trim();
        var wantMod = string.Equals(source, "mod", StringComparison.OrdinalIgnoreCase);
        var wantVanilla = string.Equals(source, "vanilla", StringComparison.OrdinalIgnoreCase);
        var qLower = q.ToLowerInvariant();
        var isTplQuery = q.Length == 24 && q.All(Uri.IsHexDigit);

        // 关键：locale 字典一次性取出复用。GetItemName 内部每次都 GetLocaleDb()→LazyLoad.Value 重新物化整张
        // 本地化表，若在 ~2 万物品的循环里逐项调用会重复物化数万次（实测单次搜索 15~20s）。这里只物化一次。
        var localeDb = localeService.GetLocaleDb();
        string ResolveName(MongoId tpl) =>
            localeDb.TryGetValue($"{tpl} Name", out var n) && n.Length > 0 ? n
            : localeDb.TryGetValue($"{tpl} ShortName", out var s) ? s
            : "";

        var hits = new List<BpItemSearchHit>();
        foreach (var (tpl, item) in databaseService.GetItems())
        {
            if (item.Type != "Item")
            {
                continue; // 跳过分类节点，仅真实物品
            }

            var isMod = baseline.IsModItem(tpl);
            if ((wantMod && !isMod) || (wantVanilla && isMod))
            {
                continue;
            }

            // 名称仅在按名称匹配时才需要解析；tpl 查询/空查询先匹配后解析，避免无谓字典查找。
            bool match;
            string name;
            if (q.Length == 0)
            {
                match = true; // 空查询 = 按过滤条件列出（受 limit 截断）
                name = ResolveName(tpl);
            }
            else if (isTplQuery)
            {
                match = tpl.ToString().Equals(q, StringComparison.OrdinalIgnoreCase);
                name = match ? ResolveName(tpl) : "";
            }
            else
            {
                name = ResolveName(tpl);
                match = name.ToLowerInvariant().Contains(qLower)
                        || tpl.ToString().Contains(qLower, StringComparison.OrdinalIgnoreCase)
                        || (item.Properties?.ShortName?.ToLowerInvariant().Contains(qLower) ?? false);
            }

            if (!match)
            {
                continue;
            }

            hits.Add(new BpItemSearchHit
            {
                Tpl = tpl.ToString(),
                Name = name,
                ShortName = item.Properties?.ShortName,
                Parent = item.Parent.ToString(),
                IsMod = isMod,
                CanSellOnRagfair = item.Properties?.CanSellOnRagfair ?? false,
            });

            if (hits.Count >= limit)
            {
                break;
            }
        }

        return hits;
    }
}
