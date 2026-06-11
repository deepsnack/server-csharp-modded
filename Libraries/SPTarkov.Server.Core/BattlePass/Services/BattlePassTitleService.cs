namespace SPTarkov.Server.Core.BattlePass;

/// <summary>
///     通行证称号对外解析入口（纯静态，无 DI 依赖——底层 <see cref="BattlePassStore"/> 全静态）。
///     <list type="bullet">
///       <item>HTTP 公开只读端点（<c>BattlePassTitleController</c>）内部调用本类。</item>
///       <item>同进程的其他 mod（如 Fika 服务端）加 <c>SPT-AccountWeb</c> 程序集引用后可直接
///             <c>BattlePassTitleApi.GetEquippedTitle(profileId)</c>，省去自查 HTTP 环回。</item>
///       <item>独立进程的 mod（如 Fika 客户端 BepInEx 插件）走 HTTP 端点。</item>
///     </list>
///     约定：图片称号尺寸 128×32 PNG 透明底，<see cref="BpTitleView.ImageUrl"/> 指向
///     <c>/battlepass/api/title-image/{id}</c>。
/// </summary>
public static class BattlePassTitleApi
{
    /// <summary>解析某玩家当前佩戴的称号；未佩戴/已撤销/目录已删/图片缺失 → null。</summary>
    public static BpTitleView? GetEquippedTitle(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return null;
        }

        var player = BattlePassStore.GetPlayerTitles(profileId);
        var equipped = player.Equipped;
        if (string.IsNullOrWhiteSpace(equipped) || !player.Owned.Contains(equipped))
        {
            return null;
        }

        var title = BattlePassStore.GetTitleCatalog().FirstOrDefault(t => t.Id == equipped);
        return title is null ? null : ToView(title);
    }

    /// <summary>批量解析（raid 内多人昵称牌一次拉取）。值可能为 null（该玩家未佩戴）。</summary>
    public static Dictionary<string, BpTitleView?> GetEquippedTitles(IEnumerable<string> profileIds)
    {
        var result = new Dictionary<string, BpTitleView?>();
        foreach (var pid in profileIds)
        {
            if (string.IsNullOrWhiteSpace(pid) || result.ContainsKey(pid))
            {
                continue;
            }

            result[pid] = GetEquippedTitle(pid);
        }

        return result;
    }

    /// <summary>把目录项转成对外视图；图片称号若文件缺失则返回 null（无法渲染）。</summary>
    public static BpTitleView? ToView(BpTitle title)
    {
        var isImage = string.Equals(title.Type, "image", StringComparison.OrdinalIgnoreCase);
        if (isImage)
        {
            if (!File.Exists(BattlePassStore.TitleImagePath(title.Id)))
            {
                return null;
            }

            return new BpTitleView
            {
                Id = title.Id,
                Name = title.Name,
                Type = "image",
                ImageUrl = $"/battlepass/api/title-image/{title.Id}",
                Width = title.Width > 0 ? title.Width : 128,
                Height = title.Height > 0 ? title.Height : 32,
            };
        }

        return new BpTitleView
        {
            Id = title.Id,
            Name = title.Name,
            Type = "text",
            Text = title.Text ?? title.Name,
            Color = title.Color,
            ColorEnd = title.ColorEnd,
        };
    }
}
