using System.Net;
using System.Reflection;
using System.Text.Json;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Services.Portal;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;

namespace SPTarkov.Server.Core.BattlePass.Portal;

/// <summary>
///     通行证 Portal 兼容 sidecar：与 <see cref="SPTarkov.Server.Core.Services.Portal.PortalBridgeService"/> 同款，
///     让"通行证管理"作为<b>独立卡片</b>出现在 SptManagerPortal 门户里，一次登录即可 SSO 免密进入
///     <c>/battlepass/admin</c>。
///     <para>
///     复用同程序集（SPT-AccountWeb）内的 Portal 组件：<see cref="PortalSsoToken"/> 验签、
///     <see cref="PortalSharedKey"/> 取共享密钥、<see cref="JtiReplayGuard"/> 防重放、
///     <see cref="PortalRegistrar"/> 自注册；SSO 通过后签发的是 <see cref="WebRegisterController.IssueAdminToken"/>
///     ——通行证管理端本就复用 WebRegister 的同一 <c>X-Admin-Token</c> 鉴权域，故 token 直接可用。
///     </para>
///     <para>
///     端口：独立读取 <c>SPT_Data/battlepass/config.json</c> 的 <c>portalBridge</c>（enabled/port/hosts），
///     默认 7794，与 WebRegister sidecar（默认 7793）错开。服主可随时修改该 json 调整端口或关闭 sidecar。
///     </para>
/// </summary>
[Injectable(InjectionType.Singleton)]
public class BattlePassPortalBridgeService(
    ISptLogger<BattlePassPortalBridgeService> logger,
    ConfigServer configServer
) : IDisposable
{
    private const string PortalAudience = "battlepass";
    private const string PortalDisplayName = "通行证管理";
    private const string PortalIcon = "bp";
    private const string AdminDefaultPath = "/battlepass/admin/index.html";

    private readonly JtiReplayGuard _replayGuard = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public void Start()
    {
        var cfg = BattlePassModConfig.Load().PortalBridge ?? new BattlePassPortalBridgeConfig();
        if (!cfg.Enabled)
        {
            logger.Info("[SPT-BattlePass] Portal 兼容 sidecar 已禁用（battlepass/config.json portalBridge.enabled=false）。");
            return;
        }

        var port = cfg.Port; // 由 config.json 直接指定，默认 7794

        var hosts = (cfg.Hosts ?? [])
            .Select(h => h?.Trim() ?? "")
            .Where(h => h.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (hosts.Count == 0) hosts = ["localhost", "127.0.0.1"];

        _listener = new HttpListener();
        foreach (var host in hosts)
        {
            _listener.Prefixes.Add($"http://{host}:{port}/");
        }

        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            logger.Error($"[SPT-BattlePass] Portal sidecar 启动失败 (绑定 {string.Join(", ", hosts)}:{port}): {ex.Message}");
            if (hosts.Any(h => h is "+" or "*" || (!h.Equals("localhost", StringComparison.OrdinalIgnoreCase) && h != "127.0.0.1")))
                logger.Warning($"[SPT-BattlePass] 提示：绑定非本机主机/通配符需要管理员权限或 netsh urlacl url=http://+:{port}/ user=Everyone。");
            return;
        }

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ServeLoop(_cts.Token));
        var displayHost = hosts.FirstOrDefault(h => h is not "+" and not "*") ?? "localhost";
        logger.Success($"[SPT-BattlePass] Portal 兼容 sidecar 已启动 (绑定 {string.Join(", ", hosts)}:{port}) → http://{displayHost}:{port}/api/portal-info");

        RegisterWithPortal(port);
    }

    private void RegisterWithPortal(int port)
    {
        var portalPort = PortalModConfig.TryReadPortalPort() ?? 7790;
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        var reg = new PortalModRegistration(
            Id: PortalAudience,
            Name: "SPT-AccountWeb",
            DisplayName: PortalDisplayName,
            Version: version,
            Port: port,
            Icon: PortalIcon,
            DefaultReturn: AdminDefaultPath);
        _ = Task.Run(async () =>
        {
            var ok = await PortalRegistrar.RegisterAsync($"http://127.0.0.1:{portalPort}", reg);
            if (ok)
                logger.Info($"[SPT-BattlePass] 已向 Portal (:{portalPort}) 注册通行证管理卡片。");
            else
                logger.Warning($"[SPT-BattlePass] 向 Portal (:{portalPort}) 注册超时未成功——Portal 可能未安装或启动过慢，门户里将看不到本工具。");
        });
    }

    private async Task ServeLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            _ = Task.Run(() => Dispatch(context), ct);
        }
    }

    private async Task Dispatch(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            var res = ctx.Response;
            res.Headers["X-Content-Type-Options"] = "nosniff";
            var method = req.HttpMethod.ToUpperInvariant();
            var path = req.Url?.AbsolutePath ?? "/";

            if (method == "GET" && path == "/api/portal-info") { await HandlePortalInfo(res); return; }
            if (method == "GET" && path == "/api/sso") { await HandlePortalSso(req, res); return; }

            res.StatusCode = 404;
            res.Close();
        }
        catch (Exception ex)
        {
            logger.Error($"[SPT-BattlePass] Portal sidecar 请求异常: {ex.Message}");
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    private async Task HandlePortalInfo(HttpListenerResponse res)
    {
        await WriteJson(res, 200, new
        {
            id = PortalAudience,
            name = "SPT-AccountWeb",
            displayName = PortalDisplayName,
            version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0",
        });
    }

    private async Task HandlePortalSso(HttpListenerRequest req, HttpListenerResponse res)
    {
        var token = req.QueryString["token"];
        var ret = req.QueryString["return"];
        if (string.IsNullOrEmpty(token)) { await WriteJson(res, 400, new { error = "缺少 token" }); return; }

        var secret = PortalSharedKey.TryGetTokenSecret();
        if (string.IsNullOrEmpty(secret)) { await WriteJson(res, 503, new { error = "未配置共享 tokenSecret" }); return; }

        var payload = PortalSsoToken.Verify(token, secret, PortalAudience);
        if (payload is null) { await WriteJson(res, 401, new { error = "token 无效或已过期" }); return; }
        if (!_replayGuard.TryAccept(payload.Jti)) { await WriteJson(res, 401, new { error = "token 重放" }); return; }

        // 通过 → 签发与 WebRegister 控制器同进程共享的 admin token（通行证管理端复用同一鉴权域），
        // 经 URL fragment 注入 admin 页（fragment 不进服务器日志、不发往服务端），admin JS 读取后存入 sessionStorage。
        var adminToken = WebRegisterController.IssueAdminToken();

        var adminPath = !string.IsNullOrEmpty(ret) && ret.StartsWith("/battlepass/", StringComparison.Ordinal)
            ? ret
            : AdminDefaultPath;
        var host = ResolveRedirectHost(req);
        var httpsPort = ResolveHttpsPort();
        var location = $"https://{host}:{httpsPort}{adminPath}#sso={Uri.EscapeDataString(adminToken)}";

        res.StatusCode = 302;
        res.Headers["Location"] = location;
        res.Close();
    }

    /// <summary>跳转 host 复用浏览器访问 sidecar 时的 Host（支持反代 X-Forwarded-Host），兜底 127.0.0.1。</summary>
    private static string ResolveRedirectHost(HttpListenerRequest req)
    {
        var forwarded = req.Headers["X-Forwarded-Host"];
        string? host = null;
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            var first = forwarded.Split(',')[0].Trim();
            if (first.StartsWith('['))
            {
                var end = first.IndexOf(']');
                host = end > 0 ? first[..(end + 1)] : first;
            }
            else
            {
                var colon = first.LastIndexOf(':');
                host = colon > 0 && first.IndexOf(':') == colon ? first[..colon] : first;
            }
        }
        if (string.IsNullOrWhiteSpace(host)) host = req.Url?.Host;
        if (string.IsNullOrWhiteSpace(host)) host = "127.0.0.1";
        if (host.Contains(':') && !host.StartsWith('[')) host = "[" + host + "]";
        return host;
    }

    /// <summary>主服务器 HTTPS 端口，来自 HttpConfig（默认 6969）。</summary>
    private int ResolveHttpsPort()
    {
        try { return configServer.GetConfig<HttpConfig>().Port; }
        catch { return 6969; }
    }

    private static async Task WriteJson(HttpListenerResponse res, int status, object payload)
    {
        res.StatusCode = status;
        res.ContentType = "application/json; charset=utf-8";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); _listener?.Close(); } catch { }
    }
}
