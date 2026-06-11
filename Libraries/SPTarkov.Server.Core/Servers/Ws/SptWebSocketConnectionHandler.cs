using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Ws;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers.Ws.Message;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;
using LogLevel = SPTarkov.Server.Core.Models.Spt.Logging.LogLevel;

namespace SPTarkov.Server.Core.Servers.Ws;

/// <summary>
///     WebSocket 连接管理（原 WebSocketConcurrencyPatch 内联）：
///     Dictionary+全局锁 → ConcurrentDictionary 无锁并发；广播按 socket 并行发送、
///     payload 只序列化一次。多人同时收通知/互发消息不再互相阻塞。
/// </summary>
[Injectable(InjectionType.Singleton)]
public class SptWebSocketConnectionHandler(
    ISptLogger<SptWebSocketConnectionHandler> logger,
    ServerLocalisationService serverLocalisationService,
    JsonUtil jsonUtil,
    ProfileHelper profileHelper,
    IEnumerable<ISptWebSocketMessageHandler> messageHandlers
) : IWebSocketConnectionHandler
{
    protected readonly ConcurrentDictionary<MongoId, ConcurrentDictionary<string, WebSocket>> _sockets = new();

    public string GetHookUrl()
    {
        return "/notifierServer/getwebsocket/";
    }

    public string GetSocketId()
    {
        return "SPT WebSocket Handler";
    }

    public Task OnConnection(WebSocket ws, HttpContext context, string sessionIdContext)
    {
        var splitUrl = context.Request.Path.Value.Split("/");
        var sessionID = new MongoId(splitUrl.Last());
        var playerProfile = profileHelper.GetFullProfile(sessionID);
        var playerInfoText = $"{playerProfile.ProfileInfo.Username} ({sessionID})";
        if (logger.IsLogEnabled(LogLevel.Debug))
        {
            logger.Debug($"[WS] Websocket connect for player: {playerInfoText} started with context: {sessionIdContext}");
        }

        var sessionSockets = _sockets.GetOrAdd(sessionID, _ => new ConcurrentDictionary<string, WebSocket>());

        if (!sessionSockets.IsEmpty && logger.IsLogEnabled(LogLevel.Debug))
        {
            logger.Debug(
                serverLocalisationService.GetText("websocket-player_reconnect", new { sessionId = playerInfoText, contextId = sessionIdContext })
            );
        }

        sessionSockets[sessionIdContext] = ws;
        if (logger.IsLogEnabled(LogLevel.Info))
        {
            logger.Info(
                serverLocalisationService.GetText("websocket-player_connected", new { sessionId = playerInfoText, contextId = sessionIdContext })
            );
        }

        return Task.CompletedTask;
    }

    public async Task OnMessage(byte[] receivedMessage, WebSocketMessageType messageType, WebSocket ws, HttpContext context)
    {
        var splitUrl = context.Request.Path.Value.Split("/");
        var sessionID = splitUrl.Last();
        if (logger.IsLogEnabled(LogLevel.Debug))
        {
            logger.Debug($"[WS] Message for session {sessionID} received. Notifying message handlers.");
        }

        foreach (var sptWebSocketMessageHandler in messageHandlers)
        {
            await sptWebSocketMessageHandler.OnSptMessage(sessionID, ws, receivedMessage);
        }
    }

    public Task OnClose(WebSocket ws, HttpContext context, string sessionIdContext)
    {
        var splitUrl = context.Request.Path.Value.Split("/");
        var sessionID = splitUrl.Last();

        if (logger.IsLogEnabled(LogLevel.Debug))
        {
            logger.Debug($"Attempting to close websocket session {sessionID} with context {sessionIdContext}");
        }

        if (_sockets.TryGetValue(sessionID, out var sessionSockets) && !sessionSockets.IsEmpty)
        {
            if (logger.IsLogEnabled(LogLevel.Debug))
            {
                logger.Debug($"Websockets for session {sessionID} entry matched, attempting to find context {sessionIdContext}");
            }

            if (!sessionSockets.TryRemove(sessionIdContext, out _))
            {
                if (logger.IsLogEnabled(LogLevel.Info))
                {
                    logger.Info(
                        $"[ws] The websocket session {sessionID} with reference: {sessionIdContext} has already been removed or reconnected"
                    );
                }
            }
            else if (logger.IsLogEnabled(LogLevel.Info))
            {
                var playerProfile = profileHelper.GetFullProfile(sessionID);
                var playerInfoText = $"{playerProfile.ProfileInfo.Username} ({sessionID})";
                logger.Info($"[ws] player: {playerInfoText} {sessionIdContext} has disconnected");
            }
        }
        else if (logger.IsLogEnabled(LogLevel.Debug))
        {
            logger.Debug(
                $"Websocket for session {sessionID} with context {sessionIdContext} does not exist on the socket map, nothing was removed"
            );
        }

        return Task.CompletedTask;
    }

    public void SendMessageToAll(WsNotificationEvent output)
    {
        if (_sockets.IsEmpty)
        {
            return;
        }

        // payload 只序列化一次，全部打开的 socket 并行发送
        byte[] payload;
        try
        {
            payload = Encoding.UTF8.GetBytes(jsonUtil.Serialize(output, output.GetType()));
        }
        catch (Exception err)
        {
            logger.Error(serverLocalisationService.GetText("websocket-message_send_failed_with_error", err.Message), err);
            return;
        }

        var openSockets = new List<WebSocket>();
        foreach (var (_, sessionSockets) in _sockets)
        {
            foreach (var (_, ws) in sessionSockets)
            {
                if (ws.State == WebSocketState.Open)
                {
                    openSockets.Add(ws);
                }
            }
        }

        if (openSockets.Count == 0)
        {
            return;
        }

        var tasks = openSockets.Select(ws => SendBytesSafelyAsync(ws, payload)).ToArray();
        Task.WhenAll(tasks).GetAwaiter().GetResult();
    }

    public void SendMessage(MongoId sessionID, WsNotificationEvent output)
    {
        try
        {
            if (!_sockets.TryGetValue(sessionID, out var sessionSockets) || sessionSockets.IsEmpty)
            {
                if (logger.IsLogEnabled(LogLevel.Debug))
                {
                    logger.Debug(serverLocalisationService.GetText("websocket-not_ready_message_not_sent", sessionID.ToString()));
                }

                return;
            }

            var openSockets = sessionSockets.Values.Where(s => s.State == WebSocketState.Open).ToArray();
            if (openSockets.Length == 0)
            {
                if (logger.IsLogEnabled(LogLevel.Debug))
                {
                    logger.Debug(serverLocalisationService.GetText("websocket-not_ready_message_not_sent", sessionID.ToString()));
                }

                return;
            }

            if (logger.IsLogEnabled(LogLevel.Debug))
            {
                logger.Debug($"Send message for {sessionID} matched {openSockets.Length} websockets. Messages being sent");
            }

            var payload = Encoding.UTF8.GetBytes(jsonUtil.Serialize(output, output.GetType()));
            var tasks = openSockets.Select(ws => SendBytesSafelyAsync(ws, payload)).ToArray();
            Task.WhenAll(tasks).GetAwaiter().GetResult();

            if (logger.IsLogEnabled(LogLevel.Debug))
            {
                logger.Debug(serverLocalisationService.GetText("websocket-message_sent"));
            }
        }
        catch (Exception err)
        {
            logger.Error(serverLocalisationService.GetText("websocket-message_send_failed_with_error", err.Message), err);
        }
    }

    public bool IsWebSocketConnected(MongoId sessionID)
    {
        return _sockets.TryGetValue(sessionID, out var sockets) && sockets.Values.Any(s => s.State == WebSocketState.Open);
    }

    public IEnumerable<WebSocket> GetSessionWebSocket(MongoId sessionID)
    {
        return _sockets.GetValueOrDefault(sessionID)?.Values.Where(s => s.State == WebSocketState.Open).ToArray() ?? [];
    }

    protected async Task SendBytesSafelyAsync(WebSocket ws, byte[] payload)
    {
        try
        {
            await ws.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception err)
        {
            logger.Error(serverLocalisationService.GetText("websocket-message_send_failed_with_error", err.Message), err);
        }
    }
}
