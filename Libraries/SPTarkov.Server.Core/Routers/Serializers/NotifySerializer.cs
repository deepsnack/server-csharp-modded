using Microsoft.AspNetCore.Http;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Utils;

namespace SPTarkov.Server.Core.Routers.Serializers;

[Injectable]
public class NotifySerializer(NotifierController notifierController, JsonUtil jsonUtil, HttpServerHelper httpServerHelper) : ISerializer
{
    public async Task Serialize(MongoId sessionID, HttpRequest req, HttpResponse resp, object? body)
    {
        var splittedUrl = req.Path.Value.Split("/");
        var tmpSessionID = splittedUrl[^1].Split("?last_id")[0];

        /*
         * Take our array of JSON message objects and cast them to JSON strings, so that they can then
         *  be sent to client as NEWLINE separated strings... yup.
         */
        var messages = await notifierController.NotifyAsync(tmpSessionID);
        var text = string.Join("\n", messages.Select(message => jsonUtil.Serialize(message)));
        httpServerHelper.SendTextJson(resp, text);
    }

    public bool CanHandle(string route)
    {
        // 原实现 route.ToUpper() 会把任意大响应体（如 /client/items 12.7MB）整体转大写再比较，
        // 每次普通压缩请求都付出 ~140ms（分配 25MB + 逐字符转换）——改为长度短路的大小写不敏感比较，
        // 非 NOTIFY 路由（长度不匹配）立即返回 false，零分配。
        return route.Equals("NOTIFY", StringComparison.OrdinalIgnoreCase);
    }
}
