using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Hideout;
using SPTarkov.Server.Core.Models.Enums.Hideout;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>
///     把后台创建的自定义藏身处配方（<see cref="BpCustomRecipe"/>）注入 hideout production 数据库。
///     OnLoad 及后台保存后调用 <see cref="Sync"/>（先移除旧注入再加，支持热更新与删除）。
///     locked=true 的配方挂通行证虚拟任务锁（<see cref="LockQuestId"/>，一个永不存在的 quest）：
///     客户端视为任务锁定不可用，直到玩家领取奖励轨 recipe 奖励把 production id 写进
///     UnlockedInfo.UnlockedProductionRecipe（既有 UnlockRecipe 链路，无需新机制）。
/// </summary>
[Injectable(InjectionType.Singleton)]
public class BattlePassRecipeSync(DatabaseService databaseService, ISptLogger<BattlePassRecipeSync> logger)
{
    /// <summary>通行证配方锁专用虚拟 questId（固定值，永不对应真实任务）。</summary>
    public static readonly MongoId LockQuestId = new("ba771e9a55c0ffee00000001");

    protected readonly HashSet<MongoId> injected = [];

    /// <summary>构建并注入（或重注入）全部自定义配方到 hideout production DB。</summary>
    public void Sync()
    {
        var recipes = databaseService.GetHideout().Production.Recipes;
        if (recipes is null)
        {
            logger.Warning("[BattlePass] hideout production 表缺失，自定义配方未注入");
            return;
        }

        // 先移除上一轮注入（含已被后台删除的），再按当前 store 重建
        recipes.RemoveAll(r => injected.Contains(r.Id));
        injected.Clear();

        var customs = BattlePassStore.GetCustomRecipes();
        var itemsDb = databaseService.GetItems();
        foreach (var custom in customs)
        {
            if (!MongoId.IsValidMongoId(custom.Id) || !MongoId.IsValidMongoId(custom.EndProduct))
            {
                logger.Warning($"[BattlePass] 自定义配方 id/endProduct 无效，跳过: {custom.Id}");
                continue;
            }

            // mod 物品兜底：产物或任一原料的 tpl 不在物品库（mod 被删除）→ 跳过整个配方的注入，
            // 配置保留不删——mod 装回后重启/重保存即自动恢复。
            if (!itemsDb.ContainsKey(new MongoId(custom.EndProduct)))
            {
                logger.Warning($"[BattlePass] 自定义配方 {custom.Id} 的产物 {custom.EndProduct} 不在物品库（mod 已删除？），本次未注入");
                continue;
            }

            var missingIngredient = custom.Ingredients.FirstOrDefault(i =>
                MongoId.IsValidMongoId(i.Tpl) && !itemsDb.ContainsKey(new MongoId(i.Tpl)));
            if (missingIngredient is not null)
            {
                logger.Warning($"[BattlePass] 自定义配方 {custom.Id} 的原料 {missingIngredient.Tpl} 不在物品库（mod 已删除？），本次未注入");
                continue;
            }

            var requirements = new List<Requirement>
            {
                new()
                {
                    AreaType = custom.AreaType,
                    RequiredLevel = Math.Max(1, custom.AreaLevel),
                    Type = "Area",
                },
            };

            foreach (var ing in custom.Ingredients)
            {
                if (!MongoId.IsValidMongoId(ing.Tpl))
                {
                    continue;
                }

                requirements.Add(ing.IsTool
                    ? new Requirement { TemplateId = new MongoId(ing.Tpl), Type = "Tool" }
                    : new Requirement
                    {
                        TemplateId = new MongoId(ing.Tpl),
                        Count = Math.Max(1, ing.Count),
                        IsEncoded = false,
                        IsFunctional = false,
                        IsSpawnedInSession = false,
                        Type = "Item",
                    });
            }

            if (custom.Locked)
            {
                requirements.Add(new Requirement { QuestId = LockQuestId, Type = "QuestComplete" });
            }

            var id = new MongoId(custom.Id);
            recipes.Add(new HideoutProduction
            {
                Id = id,
                AreaType = (HideoutAreas)custom.AreaType,
                Requirements = requirements,
                ProductionTime = Math.Max(1, custom.ProductionTime),
                EndProduct = new MongoId(custom.EndProduct),
                Count = Math.Max(1, custom.Count),
                ProductionLimitCount = 0,
                IsEncoded = false,
                Locked = custom.Locked,
                NeedFuelForAllProductionTime = false,
                Continuous = false,
                IsCodeProduction = false,
            });
            injected.Add(id);
        }

        if (injected.Count > 0)
        {
            logger.Info($"[BattlePass] 已注入 {injected.Count} 个自定义藏身处配方（锁定 {customs.Count(c => c.Locked)} 个）");
        }
    }
}
