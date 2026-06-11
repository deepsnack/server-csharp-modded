using Microsoft.AspNetCore.Mvc;
using SPTarkov.DI.Annotations;

namespace SPTarkov.Server.Core.BattlePass.Controllers;

/// <summary>
///     物品图标服务（复用 GiveUI 图标方案的"阶段一"：CDN 兜底）。
///     前端只需 <c>&lt;img src="/battlepass/api/icons/{tpl}"&gt;</c>，本接口给出同源 URL，
///     便于将来无痛升级到"阶段二"（读取本机 SPT <c>user/sptappdata/live</c> 缓存 PNG）。
///     依据：开发文档/GiveUI-物品图标渲染复用计划.md。
///
///     当前实现：校验 tpl 为合法 MongoId 后 302 跳转到公共素材站
///     <c>assets.tarkov.dev/{tpl}-base-image.webp</c>（基础物品图）。
///     字面量路由 battlepass/api/icons/{tpl} 优先级高于页面 catch-all，不会被遮蔽。
/// </summary>
[Injectable]
[ApiController]
public class BattlePassIconController
{
    [HttpGet("battlepass/api/icons/{tpl}")]
    public IActionResult Item(string tpl)
    {
        // 仅允许 24 位十六进制 MongoId，杜绝开放重定向 / 路径注入。
        if (!IsItemId(tpl))
        {
            return new NotFoundResult();
        }

        // 阶段二（待办）：先按 GiveUI 的 image-hash 逻辑查 user/sptappdata/live/index.json，
        //   命中则直接返回本地 PNG（image/png）；本机暂无物品级 cache，故先走 CDN。
        var url = $"https://assets.tarkov.dev/{tpl.ToLowerInvariant()}-base-image.webp";
        return new RedirectResult(url, permanent: false); // 302
    }

    private static bool IsItemId(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length != 24)
        {
            return false;
        }

        foreach (var c in s)
        {
            var hex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!hex)
            {
                return false;
            }
        }

        return true;
    }
}
