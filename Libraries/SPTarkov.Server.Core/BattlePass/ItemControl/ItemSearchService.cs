using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Services;

namespace SPTarkov.Server.Core.BattlePass.ItemControl;

/// <summary>物品检索：按名称/tpl 查 <c>Templates.Items</c>，可按 原版/mod 过滤。供物品页与跳蚤页共用。</summary>
[Injectable]
public class ItemSearchService(
    DatabaseService databaseService,
    LocaleService localeService,
    ItemBaselineService baseline,
    ItemHelper itemHelper
)
{
    /// <summary>
    ///     解析单个物品的展示名（主表→ch→en 三表链，与 <see cref="Search"/> 同规则）。
    ///     供配方/货架等"引用物品"的查询接口复用；LocaleService 内部 LazyLoad 缓存，单次取表 O(1)。
    /// </summary>
    public string ResolveItemName(MongoId tpl)
    {
        var localeDb = localeService.GetLocaleDb();
        var chDb = localeService.GetLocaleDb("ch");
        var enDb = localeService.GetLocaleDb("en");
        var nameKey = $"{tpl} Name";
        if (localeDb.TryGetValue(nameKey, out var n) && n.Length > 0) return n;
        if (chDb.TryGetValue(nameKey, out var cn) && cn.Length > 0) return cn;
        if (enDb.TryGetValue(nameKey, out var en) && en.Length > 0) return en;
        var shortKey = $"{tpl} ShortName";
        if (localeDb.TryGetValue(shortKey, out var s) && s.Length > 0) return s;
        if (chDb.TryGetValue(shortKey, out var cs) && cs.Length > 0) return cs;
        return enDb.TryGetValue(shortKey, out var es) ? es : "";
    }

    /// <summary>
    ///     解析展示名，<b>优先简体中文</b>（ch 表 Name→ShortName），再回退主表/英文。
    ///     供面向中文用户的网页（如通行证商店）显示物品/货币名，避免英文服务端 locale 让欧元等显示成 "Euros"。
    /// </summary>
    public string ResolveItemNameZh(MongoId tpl)
    {
        var chDb = localeService.GetLocaleDb("ch");
        var nameKey = $"{tpl} Name";
        var shortKey = $"{tpl} ShortName";
        if (chDb.TryGetValue(nameKey, out var cn) && cn.Length > 0) return cn;
        if (chDb.TryGetValue(shortKey, out var cs) && cs.Length > 0) return cs;
        return ResolveItemName(tpl);
    }

    /// <summary>
    ///     批量解析展示名（中文优先，语义与 <see cref="ResolveItemNameZh"/> 完全一致）。
    ///     <b>关键：三张 locale 表只物化一次</b>再循环复用——而 <see cref="ResolveItemNameZh"/> 单发版每次都
    ///     <c>GetLocaleDb</c>→<c>LazyLoad.Value</c> 重新反序列化整张（约两万条）locale 表；在数十个 tpl 的循环里
    ///     逐个调用会重复物化数十次，既慢又拉长与其它请求（如玩家页任务/奖励轨渲染）并发反序列化 DB 的时间窗，
    ///     放大既有的裸共享集合并发隐患。凡"一次请求解析多个 tpl"的场景（审核详情等）都应走本方法而非在外部循环单发。
    ///     返回 tpl→展示名字典；仅收录解析出非空且不等于 tpl 本身的名称，无效/未解析的 tpl 不入表。
    /// </summary>
    public Dictionary<string, string> ResolveItemNamesZh(IEnumerable<MongoId> tpls)
    {
        // 三表一次性取出（与 Search 同款复用策略），循环内不再触发任何整表物化
        var localeDb = localeService.GetLocaleDb();
        var chDb = localeService.GetLocaleDb("ch");
        var enDb = localeService.GetLocaleDb("en");

        // 内联三表链，等价于 ResolveItemNameZh(tpl)：ch Name→ch ShortName→(主/ch/en Name)→(主/ch/en ShortName)
        string Resolve(MongoId tpl)
        {
            var nameKey = $"{tpl} Name";
            var shortKey = $"{tpl} ShortName";
            if (chDb.TryGetValue(nameKey, out var cn) && cn.Length > 0) return cn;
            if (chDb.TryGetValue(shortKey, out var cs) && cs.Length > 0) return cs;
            if (localeDb.TryGetValue(nameKey, out var n) && n.Length > 0) return n;
            if (enDb.TryGetValue(nameKey, out var en) && en.Length > 0) return en;
            if (localeDb.TryGetValue(shortKey, out var s) && s.Length > 0) return s;
            return enDb.TryGetValue(shortKey, out var es) ? es : "";
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tpl in tpls)
        {
            var key = tpl.ToString();
            if (result.ContainsKey(key))
            {
                continue;
            }

            var name = Resolve(tpl);
            if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
            {
                result[key] = name;
            }
        }

        return result;
    }

    /// <summary>
    ///     批量解析展示名（主表→ch→en，语义与 <see cref="ResolveItemName"/> 完全一致）。
    ///     同 <see cref="ResolveItemNamesZh"/>：<b>三张 locale 表只物化一次</b>再循环复用，供"一次请求解析多个 tpl"
    ///     的场景（如玩家页任务/奖励轨渲染）使用，避免逐 tpl 单发触发整表反序列化。
    ///     返回 tpl→展示名字典；仅收录解析出非空且不等于 tpl 本身的名称，无效/未解析的 tpl 不入表。
    /// </summary>
    public Dictionary<string, string> ResolveItemNames(IEnumerable<MongoId> tpls)
    {
        var localeDb = localeService.GetLocaleDb();
        var chDb = localeService.GetLocaleDb("ch");
        var enDb = localeService.GetLocaleDb("en");

        // 内联三表链，等价于 ResolveItemName(tpl)：(主/ch/en Name)→(主/ch/en ShortName)
        string Resolve(MongoId tpl)
        {
            var nameKey = $"{tpl} Name";
            if (localeDb.TryGetValue(nameKey, out var n) && n.Length > 0) return n;
            if (chDb.TryGetValue(nameKey, out var cn) && cn.Length > 0) return cn;
            if (enDb.TryGetValue(nameKey, out var en) && en.Length > 0) return en;
            var shortKey = $"{tpl} ShortName";
            if (localeDb.TryGetValue(shortKey, out var s) && s.Length > 0) return s;
            if (chDb.TryGetValue(shortKey, out var cs) && cs.Length > 0) return cs;
            return enDb.TryGetValue(shortKey, out var es) ? es : "";
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tpl in tpls)
        {
            var key = tpl.ToString();
            if (result.ContainsKey(key))
            {
                continue;
            }

            var name = Resolve(tpl);
            if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
            {
                result[key] = name;
            }
        }

        return result;
    }

    /// <summary>
    ///     判断物品名是否命中查询（主表/ch/en 三表任一 Name/ShortName 包含即命中，与 <see cref="Search"/> 同语义）。
    ///     供配方等"按引用物品名检索"的查询接口复用，保证中英文查询行为与物品搜索一致。
    /// </summary>
    public bool ItemNameMatches(MongoId tpl, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var nameKey = $"{tpl} Name";
        var shortKey = $"{tpl} ShortName";
        foreach (var db in (Dictionary<string, string>[])[localeService.GetLocaleDb(), localeService.GetLocaleDb("ch"), localeService.GetLocaleDb("en")])
        {
            if (db.TryGetValue(nameKey, out var n) && n.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (db.TryGetValue(shortKey, out var s) && s.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     检索。<paramref name="source"/> = all | mod | vanilla；
    ///     <paramref name="category"/> 可选 all | weapon | equipment | weaponMod。
    ///     返回至多 <paramref name="limit"/> 条。
    /// </summary>
    public List<BpItemSearchHit> Search(string? query, string? source, int limit = 60, string? category = null)
    {
        var q = (query ?? "").Trim();
        var wantMod = string.Equals(source, "mod", StringComparison.OrdinalIgnoreCase);
        var wantVanilla = string.Equals(source, "vanilla", StringComparison.OrdinalIgnoreCase);
        var qLower = q.ToLowerInvariant();
        var isTplQuery = q.Length == 24 && q.All(Uri.IsHexDigit);

        // 关键：locale 字典一次性取出复用。GetItemName 内部每次都 GetLocaleDb()→LazyLoad.Value 重新物化整张
        // 本地化表，若在 ~2 万物品的循环里逐项调用会重复物化数万次（实测单次搜索 15~20s）。这里只物化一次。
        // 多语兜底（FikaManager 搜索模式）：mod 物品的本地化往往只注入 en（或只注入 ch）表，
        // 单查主语言会让这些物品没有名字、按名称永远搜不到；serverLocale=system 在不同机器还会解析出
        // 不同主表（中文机=ch、其他=en）。这里固定 主表→ch→en 三表链，保证中英文名都能解析、都能命中。
        var localeDb = localeService.GetLocaleDb();
        var chDb = localeService.GetLocaleDb("ch");
        var enDb = localeService.GetLocaleDb("en");
        if (ReferenceEquals(chDb, localeDb))
        {
            chDb = [];
        }

        if (ReferenceEquals(enDb, localeDb) || ReferenceEquals(enDb, chDb))
        {
            enDb = [];
        }

        string ResolveName(MongoId tpl)
        {
            var nameKey = $"{tpl} Name";
            if (localeDb.TryGetValue(nameKey, out var n) && n.Length > 0) return n;
            if (chDb.TryGetValue(nameKey, out var cn) && cn.Length > 0) return cn;
            if (enDb.TryGetValue(nameKey, out var en) && en.Length > 0) return en;
            var shortKey = $"{tpl} ShortName";
            if (localeDb.TryGetValue(shortKey, out var s) && s.Length > 0) return s;
            if (chDb.TryGetValue(shortKey, out var cs) && cs.Length > 0) return cs;
            return enDb.TryGetValue(shortKey, out var es) ? es : "";
        }

        // 搜索匹配用：展示名之外，ch / en 表的名字也要能命中（en 主表环境搜中文名、ch 主表环境搜英文名）
        bool AltNameMatch(MongoId tpl, string queryLower)
        {
            var nameKey = $"{tpl} Name";
            return (chDb.TryGetValue(nameKey, out var cn) && cn.Contains(queryLower, StringComparison.OrdinalIgnoreCase))
                || (enDb.TryGetValue(nameKey, out var en) && en.Contains(queryLower, StringComparison.OrdinalIgnoreCase));
        }

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

            if (!CategoryMatches(tpl, category))
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
                        || (item.Properties?.ShortName?.ToLowerInvariant().Contains(qLower) ?? false)
                        || AltNameMatch(tpl, qLower);
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

    private bool CategoryMatches(MongoId tpl, string? category)
    {
        return (category ?? "all").Trim().ToLowerInvariant() switch
        {
            "" or "all" => true,
            "weapon" or "weapons" => itemHelper.IsOfBaseclass(tpl, BaseClasses.WEAPON),
            "weaponmod" or "weaponmods" => IsOfAnyBaseclass(tpl, [BaseClasses.MOD, BaseClasses.FUNCTIONAL_MOD]),
            "equipment" or "equip" => IsOfAnyBaseclass(
                tpl,
                [
                    BaseClasses.ARMOR,
                    BaseClasses.ARMORED_EQUIPMENT,
                    BaseClasses.ARMOR_PLATE,
                    BaseClasses.VEST,
                    BaseClasses.HEADWEAR,
                    BaseClasses.FACE_COVER,
                    BaseClasses.HEADPHONES,
                    BaseClasses.BACKPACK,
                    BaseClasses.ARM_BAND,
                    BaseClasses.NIGHT_VISION,
                    BaseClasses.THERMAL_VISION,
                    BaseClasses.VISORS,
                ]
            ),
            _ => true,
        };
    }

    private bool IsOfAnyBaseclass(MongoId tpl, IEnumerable<MongoId> baseClasses)
    {
        return baseClasses.Any(baseClass => itemHelper.IsOfBaseclass(tpl, baseClass));
    }
}
