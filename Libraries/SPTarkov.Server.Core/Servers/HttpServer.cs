using Microsoft.AspNetCore.Http;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Servers.Http;
using SPTarkov.Server.Core.Services;

namespace SPTarkov.Server.Core.Servers;

[Injectable(InjectionType.Singleton)]
public class HttpServer(
    ConfigServer configServer,
    WebSocketServer webSocketServer,
    ProfileActivityService profileActivityService,
    IEnumerable<IHttpListener> httpListeners
)
{
    protected readonly HttpConfig HttpConfig = configServer.GetConfig<HttpConfig>();

    public async Task HandleRequest(HttpContext context, RequestDelegate next)
    {
        if (context.WebSockets.IsWebSocketRequest && webSocketServer.CanHandle(context))
        {
            await webSocketServer.OnConnection(context);
            return;
        }

        // SessionId 头回退：无 PHPSESSID cookie 但带 SessionId 请求头时回填 Cookie 头，
        // 下游所有 cookie 读取点行为保持一致（原 SPT-ProfileCore SessionHeader 内联）
        if (!context.Request.Cookies.ContainsKey("PHPSESSID") && context.Request.Headers.TryGetValue("SessionId", out var headerSession))
        {
            var headerSessionId = headerSession.FirstOrDefault();
            if (!string.IsNullOrEmpty(headerSessionId))
            {
                var existingCookie = context.Request.Headers.Cookie.ToString();
                var injected = $"PHPSESSID={headerSessionId}";
                context.Request.Headers.Cookie = string.IsNullOrEmpty(existingCookie) ? injected : $"{existingCookie}; {injected}";
            }
        }

        // Use default empty mongoId if not found in cookie
        var sessionId = context.Request.Cookies.TryGetValue("PHPSESSID", out var sessionIdString)
            ? new MongoId(sessionIdString)
            : MongoId.Empty();

        if (!string.IsNullOrEmpty(sessionIdString))
        {
            profileActivityService.SetActivityTimestamp(sessionId);
        }

        var listener = httpListeners.FirstOrDefault(listener => listener.CanHandle(sessionId, context));

        if (listener != null)
        {
            await listener.Handle(sessionId, context);
        }
        else
        {
            await next(context);
        }
    }

    public string ListeningUrl()
    {
        return $"https://{HttpConfig.Ip}:{HttpConfig.Port}";
    }
}
