using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers.Ws;
using LogLevel = SPTarkov.Server.Core.Models.Spt.Logging.LogLevel;

namespace SPTarkov.Server.Core.Servers;

[Injectable(InjectionType.Singleton)]
public class WebSocketServer(IEnumerable<IWebSocketConnectionHandler> webSocketConnectionHandler, ISptLogger<WebSocketServer> logger)
{
    public bool CanHandle(HttpContext context)
    {
        return webSocketConnectionHandler.Any(wsh => context.Request.Path.Value.Contains(wsh.GetHookUrl()));
    }

    public async Task OnConnection(HttpContext httpContext)
    {
        var socket = await httpContext.WebSockets.AcceptWebSocketAsync();
        await HandleWebSocket(httpContext, socket);
    }

    private async Task HandleWebSocket(HttpContext context, WebSocket webSocket)
    {
        var socketHandlers = webSocketConnectionHandler.Where(wsh => context.Request.Path.Value.Contains(wsh.GetHookUrl()));

        var cts = new CancellationTokenSource();
        var wsToken = cts.Token;
        var webSocketIdContext = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");

        if (logger.IsLogEnabled(LogLevel.Debug))
        {
            logger.Debug($"[WS] Notifying handlers of new websocket connection opening with reference {webSocketIdContext}");
        }

        foreach (var wsh in socketHandlers)
        {
            if (webSocket.State == WebSocketState.Open)
            {
                if (logger.IsLogEnabled(LogLevel.Debug))
                {
                    logger.Debug($"WebSocketHandler \"{wsh.GetSocketId()}\" connected");
                }
            }

            await wsh.OnConnection(webSocket, context, webSocketIdContext);
        }

        if (logger.IsLogEnabled(LogLevel.Debug))
        {
            logger.Debug($"[WS] Starting read loop for websocket reference {webSocketIdContext}");
        }

        // 读循环是纯 async（99% 时间 await ReceiveAsync），无需 LongRunning 专用线程：
        // 原 Task.Factory.StartNew(..., LongRunning) 每个连接占用一个专用线程（notifier 多客户端时线程数暴涨），
        // Task.Run 改为线程池异步任务，await 期间零线程占用；Task.Run 自动 unwrap async lambda。
        var thread = Task.Run(
            async () =>
            {
                var messageBuffer = new List<byte>();
                var receiveBuffer = new byte[1024 * 4];
                var socketClosing = false;

                while (!wsToken.IsCancellationRequested && !socketClosing)
                {
                    var segment = new ArraySegment<byte>(receiveBuffer);

                    WebSocketReceiveResult? result = null;

                    try
                    {
                        result = await webSocket.ReceiveAsync(segment, wsToken);
                    }
                    catch (WebSocketException wsException)
                    {
                        if (
                            wsException.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely
                            || webSocket.State == WebSocketState.Aborted
                            || webSocket.State == WebSocketState.Closed
                        )
                        {
                            socketClosing = true;
                            break;
                        }
                    }

                    // Continue handling here, the WebSocket is not closed so we should be good despite being null here
                    if (result == null)
                    {
                        continue;
                    }

                    // Handle graceful close of the WebSocket
                    // WebsocketSharp requires this as when Close() is called it will send a message to the WS server that it's about to close.
                    // If this is not handled an exception is thrown on the client
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        logger.Debug($"[WS] WebSocket reference {webSocketIdContext} sent close frame, stopping.");
                        await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closing..", wsToken);
                        socketClosing = true;
                        break;
                    }

                    messageBuffer.AddRange(segment.Take(result.Count));

                    if (result.EndOfMessage)
                    {
                        if (logger.IsLogEnabled(LogLevel.Debug))
                        {
                            logger.Debug(
                                $"[WS] Read loop for websocket reference {webSocketIdContext} received new message. Notifying socket handlers."
                            );
                        }

                        var message = messageBuffer.ToArray();

                        foreach (var wsh in socketHandlers)
                        {
                            await wsh.OnMessage(message, WebSocketMessageType.Text, webSocket, context);
                        }

                        messageBuffer.Clear();
                    }
                }
            },
            wsToken
        );

        // 读循环异常兜底（Task.Run unwrap 后异常会传播到 thread，避免静默丢失）
        _ = thread.ContinueWith(
            t => logger.Error($"[WS] read loop for websocket reference {webSocketIdContext} faulted: {t.Exception}"),
            TaskContinuationOptions.OnlyOnFaulted
        );

        var counter = 0;
        while (webSocket.State == WebSocketState.Open)
        {
            if (counter == 30 && logger.IsLogEnabled(LogLevel.Debug))
            {
                logger.Debug(
                    $"[WS] Websocket keep alive for reference {webSocketIdContext}. Thread state {thread.Status}. Websocket state {webSocket.State}"
                );
                counter = 0;
            }
            else
            {
                counter++;
            }

            // Keep the request thread free while waiting for the next keep-alive check.
            await Task.Delay(1000);
        }

        if (logger.IsLogEnabled(LogLevel.Debug))
        {
            logger.Debug($"[WS] State for websocket reference {webSocketIdContext} is now {webSocket.State}, closing");
        }

        // Disconnect has been received, cancel the token and send OnClose to the relevant WebSockets.
        foreach (var wsh in socketHandlers)
        {
            await cts.CancelAsync();

            if (logger.IsLogEnabled(LogLevel.Debug))
            {
                logger.Debug($"[WS] OnClose for websocket reference {webSocketIdContext} requested");
            }

            await wsh.OnClose(webSocket, context, webSocketIdContext);
        }

        if (logger.IsLogEnabled(LogLevel.Debug))
        {
            logger.Debug($"[WS] Websocket reference {webSocketIdContext} fully closed.");
        }
    }
}
