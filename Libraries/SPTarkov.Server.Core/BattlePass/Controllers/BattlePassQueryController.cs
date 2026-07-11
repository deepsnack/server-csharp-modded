using Microsoft.AspNetCore.Mvc;
using SPTarkov.Server.Core.BattlePass.Administration;
using SPTarkov.Server.Core.BattlePass.ItemControl;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.DI.Annotations;

namespace SPTarkov.Server.Core.BattlePass.Controllers;

/// <summary>
///     通行证统一数据查询接口（约定：各管理模块图形化检索物品/任务都走这里，响应形状统一，杜绝各页各写一套）。
///     鉴权复用 WebRegister admin 域（X-Admin-Token / Portal SSO）。
///     <list type="bullet">
///       <item>物品：<c>GET /battlepass/api/admin/query/items?q=&amp;source=&amp;limit=</c> → <c>{ success, items:[{tpl,name,shortName,parent,isMod,canSellOnRagfair}] }</c></item>
///       <item>任务：<c>GET /battlepass/api/admin/query/tasks?q=&amp;limit=</c> → <c>{ success, tasks:[{id,title,scope,conditionType,count,description}] }</c></item>
///     </list>
///     图标统一走 <c>/battlepass/api/icons/{tpl}</c>。前端共用 <c>picker.js</c> 渲染。
/// </summary>
[Injectable]
[ApiController]
[Route("battlepass/api/admin/query")]
public class BattlePassQueryController(ItemSearchService itemSearch, Services.DatabaseService databaseService, Services.LocaleService localeService, BattlePassAdminSessionService sessionService)
{
    // 只读的游戏数据目录检索（物品/任务/货架/配方/服装/称号），供奖励轨、任务、商人等各模块
    // 图形化填表所需。管理员与「在册协管」均可检索——协管为填奖励/配置表单必须能搜；
    // 不涉及写操作或敏感数据。ValidateToken 对失效/被撤权的协管会返回 null，从而被拒。
    private bool Auth(string? token) => sessionService.ValidateToken(token) is not null;

    /// <summary>检索物品（按名称/简称/tpl）。source = all | mod | vanilla；category = all | weapon | equipment | weaponMod。</summary>
    [HttpGet("items")]
    public object Items(
        [FromQuery] string? q,
        [FromQuery] string? source,
        [FromQuery] string? category,
        [FromQuery] int? limit,
        [FromHeader(Name = "X-Admin-Token")] string? token = null
    )
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var capped = limit is > 0 and <= 100 ? limit.Value : 30;
        return new { success = true, items = itemSearch.Search(q, source, capped, category) };
    }

    /// <summary>检索任务模板（按 id / 标题，忽略大小写）。供奖励轨/其他模块引用任务时图形化选择。</summary>
    [HttpGet("tasks")]
    public object Tasks(
        [FromQuery] string? q,
        [FromQuery] int? limit,
        [FromHeader(Name = "X-Admin-Token")] string? token = null
    )
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var capped = limit is > 0 and <= 100 ? limit.Value : 30;
        var query = (q ?? "").Trim();
        var queryLower = query.ToLowerInvariant();

        IEnumerable<BpTaskTemplate> source = BattlePassStore.GetAllTasks();
        if (query.Length > 0)
        {
            source = source.Where(t =>
                (t.Id?.ToLowerInvariant().Contains(queryLower) ?? false)
                || (t.Title?.ToLowerInvariant().Contains(queryLower) ?? false)
            );
        }

        var tasks = source
            .Take(capped)
            .Select(t => new
            {
                id = t.Id,
                title = t.Title,
                scope = t.Scope,
                conditionType = t.ConditionType,
                count = t.Count,
                description = t.Description,
            })
            .ToList();

        return new { success = true, tasks };
    }

    /// <summary>检索通行证商人货架项（按 id / 展示名 / 商品物品名 / tpl）。供奖励轨 purchaseRight 图形化选择。</summary>
    [HttpGet("offers")]
    public object Offers(
        [FromQuery] string? q,
        [FromQuery] int? limit,
        [FromHeader(Name = "X-Admin-Token")] string? token = null
    )
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var capped = limit is > 0 and <= 100 ? limit.Value : 30;
        var query = (q ?? "").Trim();

        var offers = BattlePassStore.GetOffers()
            .Select(o => new
            {
                id = o.Id,
                tpl = o.Tpl,
                // 展示名缺省时回退物品名（三表 locale），保证下拉永远有可读名称
                name = !string.IsNullOrWhiteSpace(o.Name)
                    ? o.Name
                    : MongoId.IsValidMongoId(o.Tpl) ? itemSearch.ResolveItemName(new MongoId(o.Tpl)) : o.Id,
            })
            .Where(o => query.Length == 0
                || o.id.Contains(query, StringComparison.OrdinalIgnoreCase)
                || o.tpl.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (o.name?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .Take(capped)
            .ToList();

        return new { success = true, offers };
    }

    /// <summary>
    ///     检索藏身处制造配方（按产物物品名 / production id / 产物 tpl，名称走三表 locale，兼容 mod 配方）。
    ///     供奖励轨 recipe 图形化选择。
    /// </summary>
    [HttpGet("recipes")]
    public object Recipes(
        [FromQuery] string? q,
        [FromQuery] int? limit,
        [FromHeader(Name = "X-Admin-Token")] string? token = null
    )
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var capped = limit is > 0 and <= 100 ? limit.Value : 30;
        var query = (q ?? "").Trim();

        var customIds = BattlePassStore.GetCustomRecipes().Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var hits = new List<object>();
        foreach (var recipe in databaseService.GetHideout().Production.Recipes ?? [])
        {
            // 产物名匹配走三表（主/ch/en），保证中英文查询与物品搜索行为一致
            if (query.Length > 0
                && !recipe.Id.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)
                && !recipe.EndProduct.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)
                && !itemSearch.ItemNameMatches(recipe.EndProduct, query))
            {
                continue;
            }

            var name = itemSearch.ResolveItemName(recipe.EndProduct);

            hits.Add(new
            {
                id = recipe.Id.ToString(),
                endProduct = recipe.EndProduct.ToString(),
                name,
                count = recipe.Count ?? 1,
                areaType = recipe.AreaType?.ToString() ?? "",
                isCustom = customIds.Contains(recipe.Id.ToString()),
            });

            if (hits.Count >= capped)
            {
                break;
            }
        }

        return new { success = true, recipes = hits };
    }

    /// <summary>
    ///     检索服装（按服装名 / suite id / 商人服装 offer id / trader id）。
    ///     优先聚合所有 customization_seller 商人的 suits，因此可搜索 mod 服装。
    /// </summary>
    [HttpGet("clothing")]
    public object Clothing(
        [FromQuery] string? q,
        [FromQuery] int? limit,
        [FromHeader(Name = "X-Admin-Token")] string? token = null
    )
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var capped = limit is > 0 and <= 100 ? limit.Value : 30;
        var query = (q ?? "").Trim();
        var customization = databaseService.GetCustomization();
        var traderSuitOffers = databaseService
            .GetTraders()
            .Where(trader => trader.Value.Base.CustomizationSeller.GetValueOrDefault(false))
            .SelectMany(trader => (trader.Value.Suits ?? [])
                .Select(suit => new
                {
                    SuitId = suit.SuiteId.ToString(),
                    OfferId = suit.Id.ToString(),
                    TraderId = trader.Key.ToString(),
                    IsActive = suit.IsActive ?? true,
                }))
            .ToList();

        var offerBySuit = traderSuitOffers
            .GroupBy(offer => offer.SuitId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.FirstOrDefault(offer => offer.IsActive) ?? group.First(), StringComparer.OrdinalIgnoreCase);

        var hits = new List<object>();
        foreach (var (suitId, item) in customization)
        {
            var suitIdText = suitId.ToString();
            var isTraderSuit = offerBySuit.TryGetValue(suitIdText, out var offer);
            if (!isTraderSuit && item.Parent != CustomisationTypeId.SUITS)
            {
                continue;
            }

            var name = ResolveCustomizationName(suitId, item);
            var shortName = ResolveCustomizationShortName(suitId, item);
            if (query.Length > 0
                && !suitIdText.Contains(query, StringComparison.OrdinalIgnoreCase)
                && !(name?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                && !(shortName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                && !(offer?.OfferId.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                && !(offer?.TraderId.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                continue;
            }

            hits.Add(new
            {
                id = suitIdText,
                suitId = suitIdText,
                name = string.IsNullOrWhiteSpace(name) ? suitIdText : name,
                shortName,
                parent = item.Parent,
                bodyPart = item.Properties?.BodyPart,
                side = item.Properties?.Side ?? [],
                offerId = offer?.OfferId,
                traderId = offer?.TraderId,
                isTraderSuit,
                isActive = offer?.IsActive ?? false,
            });

            if (hits.Count >= capped)
            {
                break;
            }
        }

        return new { success = true, clothing = hits };
    }

    /// <summary>检索称号（按 id / 名称 / 文本）。供奖励轨 title 图形化选择。</summary>
    [HttpGet("titles")]
    public object Titles(
        [FromQuery] string? q,
        [FromQuery] int? limit,
        [FromHeader(Name = "X-Admin-Token")] string? token = null
    )
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var capped = limit is > 0 and <= 100 ? limit.Value : 30;
        var query = (q ?? "").Trim();

        var titles = BattlePassStore.GetTitleCatalog()
            .Where(t => query.Length == 0
                || t.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                || t.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (t.Text?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .Take(capped)
            .Select(t => new
            {
                id = t.Id,
                name = t.Name,
                type = t.Type,
                text = t.Text,
            })
            .ToList();

        return new { success = true, titles };
    }

    private string ResolveCustomizationName(MongoId id, CustomizationItem item)
    {
        var name = ResolveCustomizationLocaleValue(id, item, "Name", item.Properties?.Name);
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        return ResolveCustomizationShortName(id, item);
    }

    private string ResolveCustomizationShortName(MongoId id, CustomizationItem item)
    {
        return ResolveCustomizationLocaleValue(id, item, "ShortName", item.Properties?.ShortName);
    }

    private string ResolveCustomizationLocaleValue(MongoId id, CustomizationItem item, string suffix, string? fallback)
    {
        var localeDb = localeService.GetLocaleDb();
        var chDb = localeService.GetLocaleDb("ch");
        var enDb = localeService.GetLocaleDb("en");
        foreach (var key in new[] { $"{id} {suffix}", fallback, item.Name })
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            foreach (var db in (Dictionary<string, string>[])[localeDb, chDb, enDb])
            {
                if (db.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return fallback ?? item.Name ?? id.ToString();
    }
}
