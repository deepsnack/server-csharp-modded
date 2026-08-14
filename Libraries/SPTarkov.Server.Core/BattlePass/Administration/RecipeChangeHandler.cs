using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;

namespace SPTarkov.Server.Core.BattlePass.Administration;

/// <summary>
///     自定义藏身处配方模块命令处理器：新增/编辑/删除自定义配方（custom-recipes.json）。
///     <para>产物/原料均为实体物品（tpl 必填）；应用后调用 <see cref="BattlePassRecipeSync.Sync"/>
///     热重注入藏身处生产数据库。锁定配方（locked=true）挂通行证虚拟任务锁，经奖励轨 recipe 解锁。</para>
/// </summary>
[Injectable(InjectionType.Singleton)]
public class RecipeChangeHandler(
    BattlePassRecipeSync recipeSync,
    ISptLogger<RecipeChangeHandler> logger
) : IBattlePassChangeHandler
{
    private static readonly JsonSerializerOptions SerializeOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string Module => "recipes";
    public IReadOnlyList<string> CommandTypes { get; } = ["recipe.upsert", "recipe.delete"];
    public string RequiredCapability => "recipes.submit";

    public object Normalize(string commandType, JsonElement input)
    {
        return commandType switch
        {
            "recipe.upsert" => NormalizeUpsert(input),
            "recipe.delete" => NormalizeDelete(input),
            _ => throw new ArgumentException($"未知命令类型: {commandType}"),
        };
    }

    public string? Validate(string commandType, object normalizedInput)
    {
        return commandType switch
        {
            "recipe.upsert" => ValidateUpsert((BpCustomRecipe) normalizedInput),
            "recipe.delete" => ValidateDelete((RecipeDeleteInput) normalizedInput),
            _ => $"未知命令类型: {commandType}",
        };
    }

    public string Describe(string commandType, object normalizedInput, object? currentState)
    {
        switch (commandType)
        {
            case "recipe.upsert":
                var recipe = (BpCustomRecipe) normalizedInput;
                var verb = currentState is null ? "新增" : "编辑";
                return $"{verb}自定义配方 产物={recipe.EndProduct}×{recipe.Count}（id={recipe.Id}，{recipe.Ingredients.Count} 项原料）";
            case "recipe.delete":
                var del = (RecipeDeleteInput) normalizedInput;
                return $"删除自定义配方 id={del.Id}";
            default:
                return commandType;
        }
    }

    public string GetTargetKey(string commandType, object normalizedInput)
    {
        return commandType switch
        {
            "recipe.upsert" => $"recipes:recipe:{((BpCustomRecipe) normalizedInput).Id}",
            "recipe.delete" => $"recipes:recipe:{((RecipeDeleteInput) normalizedInput).Id}",
            _ => $"recipes:unknown:{commandType}",
        };
    }

    public string GetTargetDisplayName(string commandType, object normalizedInput)
    {
        return commandType switch
        {
            "recipe.upsert" => ((BpCustomRecipe) normalizedInput).EndProduct,
            "recipe.delete" => ((RecipeDeleteInput) normalizedInput).Id,
            _ => commandType,
        };
    }

    public object? GetCurrentSnapshot(string targetKey)
    {
        // targetKey format: recipes:recipe:<id>
        var parts = targetKey.Split(':', 3);
        if (parts.Length < 3 || parts[1] != "recipe") return null;

        var id = parts[2];
        return BattlePassStore.GetCustomRecipes()
            .FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));
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
            "recipe.upsert" => ApplyUpsert((BpCustomRecipe) normalizedInput),
            "recipe.delete" => ApplyDelete((RecipeDeleteInput) normalizedInput),
            _ => throw new ArgumentException($"未知命令类型: {commandType}"),
        };
    }

    public string RestoreSnapshot(string targetKey, object? beforeSnapshot, string expectedCurrentRevision, string? auditId)
    {
        var currentRevision = GetRevision(GetCurrentSnapshot(targetKey));
        if (!string.Equals(currentRevision, expectedCurrentRevision, StringComparison.Ordinal))
            throw new ChangeConflictException($"回溯目标 {targetKey} 已发生后续改动：期望 {expectedCurrentRevision}，当前 {currentRevision}");

        var parts = targetKey.Split(':', 3);
        if (parts.Length < 3 || parts[1] != "recipe") throw new InvalidOperationException($"不支持回溯目标: {targetKey}");
        return beforeSnapshot is null
            ? ApplyDelete(new RecipeDeleteInput { Id = parts[2] })
            : ApplyUpsert(BattlePassSnapshotCodec.Deserialize<BpCustomRecipe>(beforeSnapshot));
    }

    // ---- Normalize helpers ----

    private static BpCustomRecipe NormalizeUpsert(JsonElement input)
    {
        var recipe = JsonSerializer.Deserialize<BpCustomRecipe>(input.GetRawText(), SerializeOpts)
            ?? throw new ArgumentException("无法解析配方数据");

        // 空 id 生成稳定 MongoId（保证 targetKey 稳定，审核落盘/批准全程一致）。
        recipe.Id = string.IsNullOrWhiteSpace(recipe.Id) ? new MongoId().ToString() : recipe.Id.Trim();
        recipe.EndProduct = (recipe.EndProduct ?? "").Trim();
        recipe.AreaLevel = Math.Max(1, recipe.AreaLevel);
        recipe.ProductionTime = Math.Max(1, recipe.ProductionTime);
        recipe.Count = Math.Max(1, recipe.Count);
        recipe.Ingredients ??= new List<BpRecipeIngredient>();
        foreach (var ing in recipe.Ingredients)
        {
            ing.Tpl = (ing.Tpl ?? "").Trim();
            ing.Count = Math.Max(1, ing.Count);
        }

        return recipe;
    }

    private static RecipeDeleteInput NormalizeDelete(JsonElement input)
    {
        var id = input.TryGetProperty("id", out var prop) ? prop.GetString()?.Trim() : null;
        return new RecipeDeleteInput { Id = id ?? "" };
    }

    // ---- Validate helpers ----

    private static string? ValidateUpsert(BpCustomRecipe recipe)
    {
        if (!MongoId.IsValidMongoId(recipe.EndProduct))
        {
            return "产物 tpl 无效";
        }

        if (recipe.Ingredients is null || recipe.Ingredients.Count == 0)
        {
            return "配方至少需要一项原料";
        }

        if (recipe.Ingredients.Any(i => i is null || !MongoId.IsValidMongoId(i.Tpl?.Trim()) || i.Count <= 0))
        {
            return "原料 tpl 无效或数量不是正整数";
        }

        if (recipe.ProductionTime < 1)
        {
            return "生产时长需为正整数（秒）";
        }

        return null;
    }

    private static string? ValidateDelete(RecipeDeleteInput input)
    {
        return string.IsNullOrWhiteSpace(input.Id) ? "缺少 id" : null;
    }

    // ---- Apply helpers ----

    private string ApplyUpsert(BpCustomRecipe recipe)
    {
        var recipes = BattlePassStore.GetCustomRecipes();
        recipes.RemoveAll(r => string.Equals(r.Id, recipe.Id, StringComparison.Ordinal));
        recipes.Add(recipe);
        BattlePassStore.SaveCustomRecipes(recipes);
        recipeSync.Sync(); // 热重注入藏身处生产数据库

        var saved = BattlePassStore.GetCustomRecipes()
            .FirstOrDefault(r => string.Equals(r.Id, recipe.Id, StringComparison.Ordinal));
        return GetRevision(saved);
    }

    private string ApplyDelete(RecipeDeleteInput input)
    {
        var recipes = BattlePassStore.GetCustomRecipes();
        recipes.RemoveAll(r => string.Equals(r.Id, input.Id, StringComparison.Ordinal));
        BattlePassStore.SaveCustomRecipes(recipes);
        recipeSync.Sync();
        return GetRevision(null); // 删除后目标不存在，revision 为空
    }
}

/// <summary>自定义配方删除命令的规范化输入。</summary>
public record RecipeDeleteInput
{
    public string Id { get; set; } = "";
}
