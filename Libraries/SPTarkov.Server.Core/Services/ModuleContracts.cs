using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace SPTarkov.Server.Core.Services;

/// <summary>
///     物品获取途径「移除」的服务期屏蔽索引契约（Core 侧）。
///     由 BattlePass 模块的 <c>ItemAcquisitionMaskService</c> 实现；Core 各 serve 链路只依赖本接口做只读查询，
///     不反向引用实现模块，保持单向依赖边界。
/// </summary>
public interface IItemAcquisitionMaskService
{
    /// <summary>是否存在任一服务期移除项（无则各 serve 链路可零开销跳过过滤）。</summary>
    bool HasAny { get; }

    bool IsTraderItemRemoved(MongoId traderId, MongoId tpl);

    bool IsQuestRewardRemoved(MongoId questId, string rewardGroup, MongoId tpl);

    bool IsRecipeOutputRemoved(MongoId endProduct);

    bool IsIngredientRemoved(string? recipeId, MongoId tpl);

    bool IsStartInvRemoved(string? side, MongoId tpl);
}

/// <summary>
///     原版任务「禁用」的服务期屏蔽索引契约（Core 侧）。
///     由 BattlePass 模块的 <c>QuestClientMaskService</c> 实现；<c>QuestHelper</c> 任务下发链路只依赖本接口。
/// </summary>
public interface IQuestClientMaskService
{
    /// <summary>是否存在任一被禁用任务（无则下发链路可零开销跳过过滤）。</summary>
    bool HasAny { get; }

    /// <summary>该任务是否被后台禁用（应从下发给客户端的任务表剔除）。</summary>
    bool IsQuestDisabled(MongoId questId);
}

/// <summary>
///     自定义商人（通行证商人）契约（Core 侧）。
///     由 BattlePass 模块的 <c>BattlePassTraderSync</c> 实现；<c>TraderController</c> 商人刷新链路只依赖本接口。
/// </summary>
public interface IBattlePassTraderProvider
{
    /// <summary>通行证商人 id。</summary>
    MongoId TraderId { get; }

    /// <summary>刷新该商人的过期货架（限购/库存重置），仅当 traderId 匹配本商人时由调用方触发。</summary>
    void RefreshExpiredTrader(Trader trader);
}
