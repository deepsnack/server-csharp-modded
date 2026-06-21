using System.Net;
using System.Net.Sockets;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;

namespace SPTarkov.Server.Logger;

public class SptLoggerMiddleware(
    RequestDelegate next,
    ServerLocalisationService serverLocalisationService,
    ConfigServer configServer,
    SaveServer saveServer,
    ISptLogger<SptLoggerMiddleware> logger
)
{
    protected readonly HttpConfig HttpConfig = configServer.GetConfig<HttpConfig>();

    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpConfig.LogRequests)
        {
            await next(context);
            return;
        }

        var realIp = context.Connection.RemoteIpAddress ?? IPAddress.Parse("127.0.0.1");
        LogRequest(context, realIp, IsPrivateOrLocalAddress(realIp), context.WebSockets.IsWebSocketRequest);

        try
        {
            await next(context);

            if (context.Response.StatusCode == 404)
            {
                logger.Error(serverLocalisationService.GetText("unhandled_response", context.Request.Path.ToString()));
            }
        }
        catch (Exception ex)
        {
            logger.Critical("Error handling request: " + context.Request.Path);
            logger.Critical(ex.Message);
            logger.Critical(ex.StackTrace);
#if DEBUG
            throw; // added this so we can debug something.
#endif
        }
    }

    /// <summary>
    ///     Log request - handle differently if request is local
    /// </summary>
    /// <param name="context">HttpContext of request</param>
    /// <param name="clientIp">Ip of requester</param>
    /// <param name="isLocalRequest">Is this local request</param>
    protected void LogRequest(HttpContext context, IPAddress clientIp, bool isLocalRequest, bool isWSRequest)
    {
        string text;
        if (isWSRequest)
        {
            text = isLocalRequest
                ? serverLocalisationService.GetText("websocket_request", context.Request.Path.Value)
                : serverLocalisationService.GetText("websocket_request_ip", new { ip = clientIp, url = context.Request.Path.Value });
        }
        else
        {
            text = isLocalRequest
                ? serverLocalisationService.GetText("client_request", context.Request.Path.Value)
                : serverLocalisationService.GetText("client_request_ip", new { ip = clientIp, url = context.Request.Path.Value });
        }

        logger.Info(text + BuildIdentitySuffix(context));
    }

    /// <summary>
    ///     请求日志追加玩家身份后缀 ` [账号:X | 角色:Y]`（原 SPT-ProfileCore LogIdentity 内联）。
    ///     sessionId 从 PHPSESSID cookie 或 SessionId 头解析；查不到身份时返回空串。
    /// </summary>
    protected string BuildIdentitySuffix(HttpContext context)
    {
        try
        {
            if (!context.Request.Cookies.TryGetValue("PHPSESSID", out var sessionIdString))
            {
                sessionIdString = context.Request.Headers.TryGetValue("SessionId", out var headerValues)
                    ? headerValues.FirstOrDefault()
                    : null;
            }

            if (string.IsNullOrEmpty(sessionIdString) || !MongoId.IsValidMongoId(sessionIdString))
            {
                return string.Empty;
            }

            // 用单点查询（懒加载时回退头索引），不得用 GetProfiles()——那会触发全量物化
            var sessionId = new MongoId(sessionIdString);
            var username = saveServer.GetUsernameBySessionId(sessionId);
            var nickname = saveServer.GetPmcNicknameBySessionId(sessionId);

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(username))
            {
                parts.Add($"账号: {username}");
            }
            if (!string.IsNullOrEmpty(nickname))
            {
                parts.Add($"角色: {nickname}");
            }

            return parts.Count == 0 ? string.Empty : $" [{string.Join(" | ", parts)}]";
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    ///     Check against hardcoded values that determine it's from a local address
    /// </summary>
    /// <param name="remoteAddress"> Address to check </param>
    /// <returns> True if its local </returns>
    protected bool IsPrivateOrLocalAddress(IPAddress remoteAddress)
    {
        if (IPAddress.IsLoopback(remoteAddress))
        {
            return true;
        }

        if (remoteAddress.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = remoteAddress.GetAddressBytes();

            switch (bytes[0])
            {
                case 10:
                    return true; // 10.0.0.0/8 (private)

                case 169:
                    return bytes[1] == 254; // 169.254.0.0/16 (APIPA/link-local)

                case 172:
                    return bytes[1] >= 16 && bytes[1] <= 31; // 172.16.0.0/12 (private)

                case 192:
                    return bytes[1] == 168; // 192.168.0.0/16 (private)

                default:
                    return false;
            }
        }

        if (remoteAddress.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (remoteAddress.IsIPv6LinkLocal)
            {
                return true;
            }
        }

        return false;
    }
}
