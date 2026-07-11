using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>
///     基于 Handbook 一级分类解析商品所属品类。
///     <para>分类只影响玩家页展示，不给管理后台增加配置项。</para>
///     <para>规则：沿 Handbook.Items → Categories 的 ParentId 链回溯到根节点的直接子节点，即一级分类。
///     虚拟商品归为 <c>virtual</c>，断链/无 Handbook 记录的归为 <c>other</c>。</para>
/// </summary>
[Injectable]
public class BattlePassItemCategoryService(
    DatabaseService databaseService,
    LocaleService localeService,
    ISptLogger<BattlePassItemCategoryService> logger
)
{
    /// <summary>特殊分类 Id。</summary>
    public const string VirtualCategoryId = "virtual";
    public const string OtherCategoryId = "other";

    private readonly object _indexLock = new();
    private CategoryIndex? _index;
    private int _lastItemCount;
    private int _lastCategoryCount;

    /// <summary>解析一个物品 tpl 对应的一级分类。虚拟商品传 isVirtual=true 强制归入 virtual。</summary>
    public ItemCategory Resolve(string tpl, bool isVirtual = false)
    {
        if (isVirtual)
        {
            return new ItemCategory(VirtualCategoryId, "虚拟商品", int.MaxValue - 1);
        }

        var index = GetOrBuildIndex();

        if (string.IsNullOrWhiteSpace(tpl))
        {
            return index.Other;
        }

        if (index.TplToCategory.TryGetValue(tpl, out var cat))
        {
            return cat;
        }

        // 未命中缓存：可能是后加载的 mod 物品，尝试即时查找
        var handbook = databaseService.GetHandbook();
        var item = handbook.Items.FirstOrDefault(h => string.Equals(h.Id.ToString(), tpl, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return index.Other;
        }

        var resolved = ResolveCategoryForItem(item, index.CategoryMap, index.RootIds, out _);
        if (resolved is not null)
        {
            index.TplToCategory[tpl] = resolved;
            return resolved;
        }

        return index.Other;
    }

    /// <summary>获取所有已知的一级分类列表（含 virtual 和 other），用于 ShopCatalog.Categories。</summary>
    public List<ItemCategory> GetAllCategories()
    {
        var index = GetOrBuildIndex();
        return index.AllCategories;
    }

    private CategoryIndex GetOrBuildIndex()
    {
        var handbook = databaseService.GetHandbook();
        var itemCount = handbook.Items.Count;
        var categoryCount = handbook.Categories.Count;

        lock (_indexLock)
        {
            if (_index is not null && _lastItemCount == itemCount && _lastCategoryCount == categoryCount)
            {
                return _index;
            }

            _index = BuildIndex(handbook);
            _lastItemCount = itemCount;
            _lastCategoryCount = categoryCount;
            return _index;
        }
    }

    private CategoryIndex BuildIndex(HandbookBase handbook)
    {
        var categoryMap = handbook.Categories
            .ToDictionary(c => c.Id.ToString()!, c => c, StringComparer.OrdinalIgnoreCase);

        // 只把显式无父级的分类视为真实根；父级缺失的分类是断链，不是根。
        var rootIds = FindRootIds(categoryMap);
        if (rootIds.Count == 0)
        {
            logger.Warning("BattlePassItemCategoryService: Handbook 未找到显式根分类，所有断链物品将归为「其他」");
        }

        // 一级分类：ParentId 指向任一真实根节点的直接子节点
        var firstLevelCategories = handbook.Categories
            .Where(c => c.ParentId.HasValue && rootIds.Contains(c.ParentId.Value.ToString()))
            .ToList();

        // 构建一级分类信息
        var chDb = localeService.GetLocaleDb("ch");
        var defaultDb = localeService.GetLocaleDb();
        var enDb = localeService.GetLocaleDb("en");

        var categoryInfos = new Dictionary<string, ItemCategory>(StringComparer.OrdinalIgnoreCase);
        foreach (var cat in firstLevelCategories)
        {
            var catId = cat.Id.ToString()!;
            var name = ResolveCategoryName(catId, chDb, defaultDb, enDb);
            var order = int.TryParse(cat.Order, out var o) ? o : 999;
            categoryInfos[catId] = new ItemCategory(catId, name, order);
        }

        // 为每个 item tpl 映射到一级分类
        var tplToCategory = new Dictionary<string, ItemCategory>(StringComparer.OrdinalIgnoreCase);
        var brokenChains = 0;
        var cycles = 0;
        var brokenSamples = new List<string>();

        foreach (var item in handbook.Items)
        {
            var tpl = item.Id.ToString();
            if (string.IsNullOrWhiteSpace(tpl))
            {
                continue;
            }

            var resolved = ResolveCategoryForItem(item, categoryMap, rootIds, out var failure);
            if (resolved is null)
            {
                switch (failure)
                {
                    case CategoryResolveFailure.BrokenChain:
                        brokenChains++;
                        break;
                    case CategoryResolveFailure.Cycle:
                        cycles++;
                        break;
                }

                if (failure != CategoryResolveFailure.None && brokenSamples.Count < 10)
                {
                    brokenSamples.Add(tpl);
                }
            }
            else if (categoryInfos.TryGetValue(resolved.Id, out var info))
            {
                tplToCategory[tpl] = info;
            }
        }

        if (brokenChains > 0 || cycles > 0)
        {
            logger.Warning(
                $"BattlePassItemCategoryService: Handbook 分类异常汇总 brokenChains={brokenChains}, cycles={cycles}, samples=[{string.Join(", ", brokenSamples)}]，相关物品归为「其他」"
            );
        }

        // 构建 AllCategories（排序：按 Handbook order，virtual 和 other 排最后）
        var allCategories = categoryInfos.Values.OrderBy(c => c.Order).ToList();
        allCategories.Add(new ItemCategory(VirtualCategoryId, "虚拟商品", int.MaxValue - 1));
        var other = new ItemCategory(OtherCategoryId, "其他", int.MaxValue);
        allCategories.Add(other);

        return new CategoryIndex
        {
            CategoryMap = categoryMap,
            RootIds = rootIds,
            TplToCategory = tplToCategory,
            AllCategories = allCategories,
            Other = other,
        };
    }

    /// <summary>从一个 HandbookItem 回溯到一级分类。返回 null 表示断链/循环。</summary>
    private ItemCategory? ResolveCategoryForItem(
        HandbookItem item,
        Dictionary<string, HandbookCategory> categoryMap,
        HashSet<string> rootIds,
        out CategoryResolveFailure failure
    )
    {
        failure = CategoryResolveFailure.None;
        var parentId = item.ParentId.ToString();
        if (string.IsNullOrWhiteSpace(parentId))
        {
            return null;
        }

        // 沿 ParentId 链向上回溯，找到直接位于 root 下的一级分类
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = parentId;

        while (!string.IsNullOrWhiteSpace(current))
        {
            if (!visited.Add(current))
            {
                failure = CategoryResolveFailure.Cycle;
                return null; // 循环
            }

            if (!categoryMap.TryGetValue(current, out var cat))
            {
                failure = CategoryResolveFailure.BrokenChain;
                return null; // 断链
            }

            var catParent = cat.ParentId?.ToString();

            // 如果当前节点的 parent 是任一真实根，说明当前节点是一级分类
            if (!string.IsNullOrWhiteSpace(catParent) && rootIds.Contains(catParent))
            {
                var catId = cat.Id.ToString()!;
                var order = int.TryParse(cat.Order, out var o) ? o : 999;
                return new ItemCategory(catId, "", order); // name 由调用方从 categoryInfos 查
            }

            current = catParent;
        }

        return null; // 到达根或 null 仍未命中一级分类
    }

    private static HashSet<string> FindRootIds(Dictionary<string, HandbookCategory> categoryMap)
    {
        return categoryMap
            .Where(pair => string.IsNullOrWhiteSpace(pair.Value.ParentId?.ToString()))
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveCategoryName(
        string categoryId,
        Dictionary<string, string> chDb,
        Dictionary<string, string> defaultDb,
        Dictionary<string, string> enDb)
    {
        // Handbook 一级分类的本地化名以「分类 Id」本身为 key（非 "{Id} Name"）：
        // 例如 ch.json["5b47574386f77428ca22b340"] = "给养"。三表链 主(ch)→默认→en 逐级回退。
        if (chDb.TryGetValue(categoryId, out var chName) && !string.IsNullOrWhiteSpace(chName))
        {
            return chName;
        }

        if (defaultDb.TryGetValue(categoryId, out var defName) && !string.IsNullOrWhiteSpace(defName))
        {
            return defName;
        }

        if (enDb.TryGetValue(categoryId, out var enName) && !string.IsNullOrWhiteSpace(enName))
        {
            return enName;
        }

        return "其他";
    }

    private sealed class CategoryIndex
    {
        public Dictionary<string, HandbookCategory> CategoryMap { get; init; } = new();
        public HashSet<string> RootIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ItemCategory> TplToCategory { get; init; } = new();
        public List<ItemCategory> AllCategories { get; init; } = new();
        public ItemCategory Other { get; init; } = new(OtherCategoryId, "其他", int.MaxValue);
    }

    private enum CategoryResolveFailure
    {
        None,
        BrokenChain,
        Cycle,
    }
}

/// <summary>一级物品分类（只读展示用）。</summary>
public record ItemCategory(string Id, string Name, int Order);
