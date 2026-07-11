using System.Net;
using System.Text;
using System.Text.Json;

namespace SPTarkov.Server.Core.BattlePass.Administration;

/// <summary>
///     审核变更「自动解析模块」：把 <see cref="BpChangeRequest"/> 解析成人类可读的邮件主题与 HTML 正文，
///     供审核详情和通知邮件复用。
///     <para>
///     纯静态、无副作用、无 DI 依赖。物品 tpl → 展示名由调用方注入 <c>resolveItemName</c> 委托（可空，
///     解析失败/未提供时回退 name→tpl）。所有输出经 HTML 转义，字段名映射为中文，与前端审核页 reviews.js 的渲染一致。
///     </para>
/// </summary>
public static class BattlePassChangeDigest
{
    private static readonly Dictionary<string, string> ModuleLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["shop"] = "商店",
        ["tasks"] = "任务",
        ["tracks"] = "奖励轨",
        ["lottery"] = "抽奖",
        ["trader"] = "商人",
        ["recipes"] = "配方",
        ["items"] = "物品管控",
        ["flea"] = "跳蚤",
    };

    private static readonly Dictionary<string, string> OperationLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["create"] = "新增",
        ["update"] = "修改",
        ["delete"] = "删除",
    };

    // 奖励类型标签（与前端 reviews.js TYPE_LABELS 对齐）
    private static readonly Dictionary<string, string> TypeLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["item"] = "物品",
        ["purchaseRight"] = "商人权益",
        ["recipe"] = "配方",
        ["title"] = "称号",
        ["clothing"] = "服装",
        ["lotteryGlobalTickets"] = "通用抽奖券",
        ["lotteryPoolTickets"] = "奖池券",
        ["lotteryExchangeCoins"] = "兑换币",
    };

    // 字段名 → 中文（与前端 reviews.js FIELD_LABELS 对齐）
    private static readonly Dictionary<string, string> FieldLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = "ID",
        ["seasonId"] = "赛季",
        ["name"] = "名称",
        ["displayName"] = "名称",
        ["title"] = "标题",
        ["label"] = "标签",
        ["desc"] = "描述",
        ["description"] = "描述",
        ["note"] = "备注",
        ["text"] = "文本",
        ["tpl"] = "物品模板",
        ["count"] = "数量",
        ["amount"] = "数量",
        ["qty"] = "数量",
        ["quantity"] = "数量",
        ["price"] = "价格",
        ["cost"] = "花费",
        ["currency"] = "货币",
        ["currencyTpl"] = "货币",
        ["type"] = "类型",
        ["kind"] = "种类",
        ["enabled"] = "启用",
        ["disabled"] = "禁用",
        ["active"] = "启用",
        ["level"] = "等级",
        ["minLevel"] = "最低等级",
        ["maxLevel"] = "等级上限",
        ["xp"] = "经验",
        ["exp"] = "经验",
        ["cycleXp"] = "循环经验",
        ["free"] = "免费轨",
        ["premium"] = "付费轨",
        ["reward"] = "奖励",
        ["rewards"] = "奖励",
        ["offerId"] = "货架ID",
        ["recipeId"] = "配方ID",
        ["titleId"] = "称号ID",
        ["suitId"] = "服装ID",
        ["poolId"] = "奖池",
        ["featured"] = "核心大奖",
        ["foundInRaid"] = "战局内找到",
        ["target"] = "目标",
        ["targetId"] = "目标ID",
        ["condition"] = "条件",
        ["conditions"] = "条件",
        ["weight"] = "权重",
        ["chance"] = "概率",
        ["tier"] = "档位",
        ["category"] = "分类",
        ["group"] = "分组",
        ["tasks"] = "任务",
        ["items"] = "物品",
        ["pools"] = "奖池",
        ["offers"] = "货架",
        ["requirements"] = "需求",
        ["startTime"] = "开始时间",
        ["endTime"] = "结束时间",
        ["period"] = "周期",
        ["refreshPeriod"] = "刷新周期",
    };

    public static string ModuleLabel(string? module) => ModuleLabels.GetValueOrDefault(module ?? "", module ?? "未知模块");

    public static string OperationLabel(string? op) => OperationLabels.GetValueOrDefault(op ?? "", op ?? "变更");

    /// <summary>邮件主题：谁 提交了 什么模块的什么操作。</summary>
    public static string BuildSubject(BpChangeRequest c)
    {
        var actor = string.IsNullOrWhiteSpace(c.Actor?.DisplayName) ? "协管" : c.Actor!.DisplayName;
        return $"[通行证审核] {actor} 提交了{ModuleLabel(c.Module)}的{OperationLabel(c.Operation)}";
    }

    /// <summary>
    ///     邮件 HTML 正文：元信息表 + 更改内容易读渲染。
    ///     <paramref name="resolveItemName"/>：tpl → 展示名（中文优先）；null 或返回空时回退 name→tpl。
    /// </summary>
    public static string BuildHtmlBody(BpChangeRequest c, Func<string, string?>? resolveItemName = null)
    {
        var actor = string.IsNullOrWhiteSpace(c.Actor?.DisplayName) ? "协管" : c.Actor!.DisplayName;
        var time = c.CreatedUtc > 0
            ? DateTimeOffset.FromUnixTimeSeconds(c.CreatedUtc).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")
            : "-";

        var sb = new StringBuilder();
        sb.Append("<div style=\"font-family:'Segoe UI',Arial,'Microsoft YaHei',sans-serif;max-width:720px;margin:0 auto;color:#24292f;line-height:1.6;\">");
        sb.Append("<h2 style=\"margin:0 0 6px;font-size:18px;color:#1f2328;\">通行证协管提交待审核</h2>");
        sb.Append($"<p style=\"margin:0 0 16px;color:#57606a;\">{Enc(actor)} 提交了<b>{Enc(ModuleLabel(c.Module))}</b>的<b>{Enc(OperationLabel(c.Operation))}</b>，请登录通行证管理后台审核。</p>");

        // ---- 元信息表 ----
        sb.Append("<table style=\"border-collapse:collapse;width:100%;margin:0 0 18px;font-size:14px;\">");
        MetaRow(sb, "模块", ModuleLabel(c.Module));
        MetaRow(sb, "操作", OperationLabel(c.Operation));
        if (!string.IsNullOrWhiteSpace(c.TargetDisplayName)) MetaRow(sb, "目标", c.TargetDisplayName);
        MetaRow(sb, "提交人", actor);
        MetaRow(sb, "提交时间", time);
        if (!string.IsNullOrWhiteSpace(c.Summary)) MetaRow(sb, "摘要", c.Summary);
        MetaRow(sb, "变更ID", c.Id);
        sb.Append("</table>");

        // ---- 更改内容 ----
        sb.Append("<h3 style=\"margin:0 0 8px;font-size:15px;color:#1f2328;border-left:3px solid #0969da;padding-left:8px;\">更改内容</h3>");
        try
        {
            if (string.Equals(c.Module, "tracks", StringComparison.OrdinalIgnoreCase))
            {
                var before = ToElement(c.BeforePayload);
                var proposed = ToElement(c.FinalPayload ?? c.ProposedPayload);
                sb.Append(RenderTracks(before, proposed, resolveItemName));
            }
            else if (string.Equals(c.Operation, "delete", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append("<p style=\"color:#cf222e;margin:0 0 6px;\">将删除以下对象：</p>");
                sb.Append(RenderElement(ToElement(c.BeforePayload), resolveItemName));
            }
            else
            {
                sb.Append(RenderElement(ToElement(c.FinalPayload ?? c.ProposedPayload ?? c.BeforePayload), resolveItemName));
            }
        }
        catch
        {
            // 解析失败不阻断通知：至少给出摘要
            sb.Append($"<p style=\"color:#57606a;\">{Enc(c.Summary)}</p>");
        }

        sb.Append("<hr style=\"border:none;border-top:1px solid #d0d7de;margin:18px 0 10px;\">");
        sb.Append("<p style=\"font-size:12px;color:#8c959f;margin:0;\">本邮件由通行证审核系统自动发送。审核详情与「变更前/变更后」完整对比请在管理后台的审核页查看。</p>");
        sb.Append("</div>");
        return sb.ToString();
    }

    // ============================ 渲染辅助 ============================

    /// <summary>
    ///     收集变更三段 payload（前 / 提交 / 最终）中引用的全部物品 tpl（含 currencyTpl 货币），
    ///     供 Web 审核页批量解析为展示名——使审核详情显示物品名而非裸 MongoId。递归遍历，去重、大小写不敏感。
    /// </summary>
    public static IReadOnlyCollection<string> CollectItemTpls(BpChangeRequest c)
    {
        var acc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectTpls(ToElement(c.BeforePayload), acc);
        CollectTpls(ToElement(c.ProposedPayload), acc);
        CollectTpls(ToElement(c.FinalPayload), acc);
        return acc;
    }

    private static void CollectTpls(JsonElement v, HashSet<string> acc)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in v.EnumerateObject())
                {
                    if ((string.Equals(p.Name, "tpl", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(p.Name, "currencyTpl", StringComparison.OrdinalIgnoreCase))
                        && p.Value.ValueKind == JsonValueKind.String)
                    {
                        var s = p.Value.GetString();
                        if (!string.IsNullOrEmpty(s))
                        {
                            acc.Add(s);
                        }
                    }
                    else
                    {
                        CollectTpls(p.Value, acc);
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var it in v.EnumerateArray())
                {
                    CollectTpls(it, acc);
                }

                break;
        }
    }

    private static void MetaRow(StringBuilder sb, string k, string v)
    {
        sb.Append("<tr>");
        sb.Append($"<td style=\"padding:5px 12px 5px 0;color:#8c959f;white-space:nowrap;vertical-align:top;width:84px;\">{Enc(k)}</td>");
        sb.Append($"<td style=\"padding:5px 0;color:#1f2328;\">{Enc(v)}</td>");
        sb.Append("</tr>");
    }

    /// <summary>递归渲染任意 JSON 值为易读 HTML。</summary>
    private static string RenderElement(JsonElement v, Func<string, string?>? resolve)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.Object:
                if (IsReward(v))
                {
                    return $"<div>{RewardChip(v, resolve)}</div>";
                }

                var rows = new StringBuilder();
                foreach (var p in v.EnumerateObject())
                {
                    if (IsEmpty(p.Value))
                    {
                        continue;
                    }

                    rows.Append("<tr>");
                    rows.Append($"<td style=\"padding:4px 12px 4px 0;color:#8c959f;white-space:nowrap;vertical-align:top;\">{Enc(FieldLabel(p.Name))}</td>");
                    rows.Append($"<td style=\"padding:4px 0;color:#1f2328;\">{RenderValue(p.Name, p.Value, resolve)}</td>");
                    rows.Append("</tr>");
                }

                return rows.Length > 0
                    ? $"<table style=\"border-collapse:collapse;font-size:14px;margin:0 0 6px;\">{rows}</table>"
                    : "<span style=\"color:#8c959f;\">（空）</span>";

            case JsonValueKind.Array:
                return RenderValue("", v, resolve);

            default:
                return Scalar(v);
        }
    }

    /// <summary>按字段键渲染值：奖励轨/奖励数组走芯片，其余按类型递归。</summary>
    private static string RenderValue(string key, JsonElement v, Func<string, string?>? resolve)
    {
        if (v.ValueKind == JsonValueKind.Array)
        {
            var items = v.EnumerateArray().ToList();
            if (items.Count == 0)
            {
                return "<span style=\"color:#8c959f;\">（无）</span>";
            }

            // 奖励轨 / 奖励列表 → 芯片
            if (items.All(IsReward))
            {
                return $"<div>{string.Concat(items.Select(x => RewardChip(x, resolve)))}</div>";
            }

            // 对象数组 → 逐项子表
            if (items[0].ValueKind == JsonValueKind.Object)
            {
                var blocks = new StringBuilder();
                foreach (var it in items)
                {
                    blocks.Append($"<div style=\"margin:0 0 6px;padding:6px 10px;background:#f6f8fa;border-radius:6px;\">{RenderElement(it, resolve)}</div>");
                }

                return blocks.ToString();
            }

            // 标量数组 → 顿号连接
            return Enc(string.Join("、", items.Select(ScalarText)));
        }

        if (v.ValueKind == JsonValueKind.Object)
        {
            if (IsReward(v))
            {
                return RewardChip(v, resolve);
            }

            return RenderElement(v, resolve);
        }

        return Scalar(v);
    }

    /// <summary>奖励轨专用：仅列出提交（差量）的等级，逐轨对比「变更前 → 变更后」。</summary>
    private static string RenderTracks(JsonElement before, JsonElement proposed, Func<string, string?>? resolve)
    {
        if (proposed.ValueKind != JsonValueKind.Object)
        {
            return "<span style=\"color:#8c959f;\">（无等级改动）</span>";
        }

        // 等级键排序：数字升序，"0"（循环奖励）置末
        var keys = proposed.EnumerateObject()
            .Select(p => p.Name)
            .OrderBy(k => k == "0" ? int.MaxValue : (int.TryParse(k, out var n) ? n : int.MaxValue - 1))
            .ToList();
        if (keys.Count == 0)
        {
            return "<span style=\"color:#8c959f;\">（无等级改动）</span>";
        }

        var sb = new StringBuilder();
        sb.Append($"<p style=\"color:#57606a;margin:0 0 8px;\">共 {keys.Count} 个等级改动：</p>");
        foreach (var k in keys)
        {
            var lvName = k == "0" ? "循环奖励（满级后）" : $"等级 {k}";
            TryProp(proposed, k, out var aLevel);
            var hasBefore = TryProp(before, k, out var bLevel);

            sb.Append("<div style=\"margin:0 0 10px;padding:8px 12px;background:#f6f8fa;border-radius:6px;\">");
            sb.Append($"<div style=\"font-weight:600;color:#1f2328;margin:0 0 6px;\">{Enc(lvName)}</div>");
            sb.Append(TrackRow("免费轨", hasBefore ? GetArray(bLevel, "free") : null, GetArray(aLevel, "free"), resolve));
            sb.Append(TrackRow("付费轨", hasBefore ? GetArray(bLevel, "premium") : null, GetArray(aLevel, "premium"), resolve));
            sb.Append("</div>");
        }

        return sb.ToString();
    }

    private static string TrackRow(string label, List<JsonElement>? before, List<JsonElement>? after, Func<string, string?>? resolve)
    {
        before ??= new();
        after ??= new();
        if (before.Count == 0 && after.Count == 0)
        {
            return "";
        }

        string Chips(List<JsonElement> arr) => arr.Count > 0
            ? string.Concat(arr.Select(x => RewardChip(x, resolve)))
            : "<span style=\"color:#8c959f;\">（无）</span>";

        var sb = new StringBuilder();
        sb.Append("<div style=\"margin:0 0 6px;\">");
        sb.Append($"<span style=\"display:inline-block;min-width:52px;color:#8c959f;font-size:13px;\">{Enc(label)}</span>");
        sb.Append($"<span style=\"color:#8c959f;font-size:12px;\">前：</span>{Chips(before)}");
        sb.Append("<span style=\"color:#8c959f;font-size:12px;margin:0 4px;\">→</span>");
        sb.Append($"<span style=\"color:#8c959f;font-size:12px;\">后：</span>{Chips(after)}");
        sb.Append("</div>");
        return sb.ToString();
    }

    /// <summary>单个奖励 → 类型标签 + 名称（name→中文名→tpl 兜底）+ 数量。</summary>
    private static string RewardChip(JsonElement rw, Func<string, string?>? resolve)
    {
        var type = Str(rw, "type") ?? "item";
        var label = TypeLabels.GetValueOrDefault(type, type);
        var name = Str(rw, "name");
        var tpl = Str(rw, "tpl");

        string? resolved = null;
        if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(tpl) && resolve is not null)
        {
            try { resolved = resolve(tpl); } catch { resolved = null; }
        }

        var title = FirstNonEmpty(name, resolved, tpl,
            Str(rw, "titleId"), Str(rw, "suitId"), Str(rw, "recipeId"), Str(rw, "offerId"), Str(rw, "poolId")) ?? "(未命名)";

        var cnt = "";
        if (TryProp(rw, "count", out var cv) && cv.ValueKind == JsonValueKind.Number && cv.TryGetInt32(out var n) && n > 1)
        {
            cnt = $"<span style=\"color:#0969da;font-weight:600;\"> ×{n}</span>";
        }

        return "<span style=\"display:inline-block;margin:2px 4px 2px 0;padding:2px 8px;background:#eaeef2;border-radius:12px;font-size:13px;\">"
            + $"<span style=\"color:#8c959f;\">{Enc(label)}</span> {Enc(title)}{cnt}</span>";
    }

    // ============================ 低层工具 ============================

    private static JsonElement ToElement(object? o)
    {
        if (o is null)
        {
            return default;
        }

        if (o is JsonElement je)
        {
            return je;
        }

        // 内存强类型对象（如从 Submit 直接传入）→ 序列化为 JsonElement 统一处理
        return JsonSerializer.SerializeToElement(o, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    /// <summary>大小写不敏感取属性（存储为 camelCase，但兼容其它策略）。</summary>
    private static bool TryProp(JsonElement o, string name, out JsonElement val)
    {
        if (o.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in o.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    val = p.Value;
                    return true;
                }
            }
        }

        val = default;
        return false;
    }

    private static string? Str(JsonElement o, string name) =>
        TryProp(o, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static List<JsonElement>? GetArray(JsonElement o, string name) =>
        TryProp(o, name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToList() : null;

    private static bool IsReward(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var type = Str(v, "type");
        if (type is null || !TypeLabels.ContainsKey(type))
        {
            return false;
        }

        return TryProp(v, "tpl", out _) || TryProp(v, "count", out _) || TryProp(v, "offerId", out _)
            || TryProp(v, "titleId", out _) || TryProp(v, "suitId", out _) || TryProp(v, "recipeId", out _)
            || TryProp(v, "poolId", out _);
    }

    private static bool IsEmpty(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        JsonValueKind.String => string.IsNullOrEmpty(v.GetString()),
        JsonValueKind.Array => !v.EnumerateArray().Any(),
        _ => false,
    };

    private static string FieldLabel(string key) => FieldLabels.GetValueOrDefault(key, key);

    private static string Scalar(JsonElement v) => Enc(ScalarText(v));

    private static string ScalarText(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => "是",
        JsonValueKind.False => "否",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.String => v.GetString() ?? "",
        _ => v.GetRawText(),
    };

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

    private static string Enc(string? s) => WebUtility.HtmlEncode(s ?? "");
}
