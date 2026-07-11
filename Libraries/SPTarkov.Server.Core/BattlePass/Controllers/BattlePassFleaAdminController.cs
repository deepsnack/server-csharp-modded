using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SPTarkov.Server.Core.BattlePass.Administration;
using SPTarkov.Server.Core.BattlePass.ItemControl;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.DI.Annotations;

namespace SPTarkov.Server.Core.BattlePass.Controllers;

/// <summary>
///     跳蚤黑名单接管管理端 API：读写 flea-control 配置（保存即应用到 RagfairConfig），
///     物品搜索复用 <see cref="ItemSearchService"/>。鉴权统一走
///     <see cref="BattlePassAdminSessionService.ValidateToken"/>（兼容原始 token 与会话 token）。
/// </summary>
[Injectable]
[ApiController]
[Route("battlepass/api/admin/flea")]
public class BattlePassFleaAdminController(
    ItemSearchService searchService,
    FleaControlSync fleaSync,
    DatabaseService databaseService,
    ConfigServer configServer,
    ItemBaselineService baseline,
    BattlePassAdminSessionService sessionService
)
{
    private bool Auth(string? token) => sessionService.ValidateToken(token)?.IsAdmin == true;
    private bool CanRead(string? token, string capability) => sessionService.ValidateToken(token)?.HasCapability(capability) == true;

    [HttpGet("config")]
    public object GetConfig([FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!CanRead(token, "flea.read"))
        {
            return new { success = false, message = "未授权" };
        }

        return new { success = true, config = BattlePassStore.GetFleaControl() };
    }

    [HttpPost("config")]
    public object SaveConfig([FromBody] BpFleaControl config, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        BattlePassStore.SaveFleaControl(config);
        fleaSync.Apply();
        return new { success = true };
    }

    /// <summary>
    ///     增量增删单个物品的跳蚤黑名单（供物品管控页快捷拉黑，无需整表替换）。
    ///     body：{ tpl: string, add?: bool=true }。保存即应用到 RagfairConfig。
    /// </summary>
    [HttpPost("blacklist/toggle")]
    public object ToggleBlacklist([FromBody] JsonElement request, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var tpl = request.TryGetProperty("tpl", out var t) ? t.GetString()?.Trim() : null;
        if (string.IsNullOrWhiteSpace(tpl))
        {
            return new { success = false, message = "缺少 tpl" };
        }

        var add = !request.TryGetProperty("add", out var a) || a.ValueKind != JsonValueKind.False;

        var cfg = BattlePassStore.GetFleaControl();
        var changed = add ? cfg.BlacklistTpls.Add(tpl) : cfg.BlacklistTpls.Remove(tpl);
        if (changed)
        {
            BattlePassStore.SaveFleaControl(cfg);
            fleaSync.Apply();
        }

        return new { success = true, blacklisted = add, count = cfg.BlacklistTpls.Count };
    }

    /// <summary>
    ///     增量增删单个物品的白名单（强制放开）。用于在「有效黑名单」列表里放开 BSG 官方/原版自定义禁售项。
    ///     body：{ tpl: string, add?: bool=true }。
    /// </summary>
    [HttpPost("whitelist/toggle")]
    public object ToggleWhitelist([FromBody] JsonElement request, [FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!Auth(token))
        {
            return new { success = false, message = "未授权" };
        }

        var tpl = request.TryGetProperty("tpl", out var t) ? t.GetString()?.Trim() : null;
        if (string.IsNullOrWhiteSpace(tpl))
        {
            return new { success = false, message = "缺少 tpl" };
        }

        var add = !request.TryGetProperty("add", out var a) || a.ValueKind != JsonValueKind.False;

        var cfg = BattlePassStore.GetFleaControl();
        var changed = add ? cfg.WhitelistTpls.Add(tpl) : cfg.WhitelistTpls.Remove(tpl);
        if (changed)
        {
            BattlePassStore.SaveFleaControl(cfg);
            fleaSync.Apply();
        }

        return new { success = true, whitelisted = add, count = cfg.WhitelistTpls.Count };
    }

    /// <summary>
    ///     有效全量跳蚤黑名单：枚举真实物品，凡命中运行时 <c>RagfairConfig.Dynamic.Blacklist.Custom</c>
    ///     或（开启 BSG 列表时）<c>CanSellOnRagfair == false</c> 者均计入，按来源标注 managed/custom/bsg。
    ///     白名单放开项因运行时已被还原可售并移出 Custom，自然不在此列。BSG 组较大，按 <c>bsgCap</c> 截断。
    /// </summary>
    [HttpGet("blacklist/effective")]
    public object GetEffectiveBlacklist([FromHeader(Name = "X-Admin-Token")] string? token = null)
    {
        if (!CanRead(token, "flea.read"))
        {
            return new { success = false, message = "未授权" };
        }

        var fleaCfg = BattlePassStore.GetFleaControl();
        var managed = fleaCfg.BlacklistTpls;
        var blacklist = configServer.GetConfig<RagfairConfig>().Dynamic.Blacklist;

        var managedTpls = new List<MongoId>();
        var customTpls = new List<MongoId>();
        var bsgTpls = new List<MongoId>();

        foreach (var (tpl, item) in databaseService.GetItems())
        {
            if (item.Type != "Item" || item.Properties is null)
            {
                continue;
            }

            var inCustom = blacklist.Custom.Contains(tpl);
            var bsgBan = blacklist.EnableBsgList && item.Properties.CanSellOnRagfair == false;
            if (!inCustom && !bsgBan)
            {
                continue;
            }

            if (inCustom && managed.Contains(tpl.ToString()))
            {
                managedTpls.Add(tpl);
            }
            else if (inCustom)
            {
                customTpls.Add(tpl);
            }
            else
            {
                bsgTpls.Add(tpl);
            }
        }

        const int bsgCap = 1500;
        var truncated = bsgTpls.Count > bsgCap;

        object Row(MongoId t, string reason) => new
        {
            tpl = t.ToString(),
            name = searchService.ResolveItemName(t),
            reason,
            isMod = baseline.IsModItem(t),
        };

        var rows = new List<object>();
        rows.AddRange(managedTpls.Select(t => Row(t, "managed")));
        rows.AddRange(customTpls.Select(t => Row(t, "custom")));
        rows.AddRange(bsgTpls.Take(bsgCap).Select(t => Row(t, "bsg")));

        return new
        {
            success = true,
            counts = new
            {
                managed = managedTpls.Count,
                custom = customTpls.Count,
                bsg = bsgTpls.Count,
                total = managedTpls.Count + customTpls.Count + bsgTpls.Count,
            },
            truncated,
            items = rows,
        };
    }

    [HttpGet("search")]
    public object Search(
        [FromQuery] string? q,
        [FromQuery] string? source,
        [FromHeader(Name = "X-Admin-Token")] string? token = null
    )
    {
        if (!CanRead(token, "flea.read"))
        {
            return new { success = false, message = "未授权" };
        }

        return new { success = true, items = searchService.Search(q, source) };
    }
}
