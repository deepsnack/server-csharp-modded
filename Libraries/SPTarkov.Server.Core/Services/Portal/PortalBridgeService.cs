using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;

namespace SPTarkov.Server.Core.Services.Portal;

/// <summary>
/// Portal 兼容 sidecar：一个独立的轻量 http 监听端口（默认 7793），只承担与 SptManagerPortal 的握手——
/// 暴露 <c>/api/portal-info</c> 与 <c>/api/sso</c>，并在启动后自注册到 Portal。
/// <para>
/// 之所以需要独立 http 端口：WebRegister 的管理页跑在主服务器 <b>HTTPS 6969</b> 的 <c>/register/*</c> 子路径下，
/// 而 Portal 的探测/跳转写死 <c>http://host:port/api/portal-info</c>（http + 根路径），二者不兼容。sidecar 用一个
/// 纯 http 端点满足 Portal 契约，验证 Portal 短 token 后签发 WebRegister 自己的 admin token（与控制器同进程共享），
/// 再 302 跳到主服务器 https 的 admin 页。本方案不需要改动 Portal。
/// </para>
/// </summary>
[Injectable(InjectionType.Singleton)]
public class PortalBridgeService(ISptLogger<PortalBridgeService> logger, ConfigServer configServer, IAdminTokenService adminTokenService) : IDisposable
{
    private const string PortalAudience = "webregister";
    private const string PortalDisplayName = "网页注册管理";
    private const string PortalIcon = "reg";
    private const string AdminDefaultPath = "/register/admin/index.html";

    private readonly JtiReplayGuard _replayGuard = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public void Start()
    {
        var cfg = WebRegisterModConfig.Load().PortalBridge ?? new PortalBridgeConfig();
        if (!cfg.Enabled)
        {
            logger.Info("[WebRegister] Portal 兼容 sidecar 已禁用（config.json portalBridge.enabled=false）。");
            return;
        }

        var hosts = (cfg.Hosts ?? [])
            .Select(h => h?.Trim() ?? "")
            .Where(h => h.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (hosts.Count == 0) hosts = ["localhost", "127.0.0.1"];

        _listener = new HttpListener();
        foreach (var host in hosts)
        {
            _listener.Prefixes.Add($"http://{host}:{cfg.Port}/");
        }

        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            logger.Error($"[WebRegister] Portal sidecar 启动失败 (绑定 {string.Join(", ", hosts)}:{cfg.Port}): {ex.Message}");
            if (hosts.Any(h => h is "+" or "*" || (!h.Equals("localhost", StringComparison.OrdinalIgnoreCase) && h != "127.0.0.1")))
                logger.Warning($"[WebRegister] 提示：绑定非本机主机/通配符需要管理员权限或 netsh urlacl url=http://+:{cfg.Port}/ user=Everyone。");
            return;
        }

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ServeLoop(_cts.Token));
        var displayHost = hosts.FirstOrDefault(h => h is not "+" and not "*") ?? "localhost";
        logger.Success($"[WebRegister] Portal 兼容 sidecar 已启动 (绑定 {string.Join(", ", hosts)}:{cfg.Port}) → http://{displayHost}:{cfg.Port}/api/portal-info");

        RegisterWithPortal(cfg.Port);
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
                logger.Info($"[WebRegister] 已向 Portal (:{portalPort}) 注册管理后台卡片。");
            else
                logger.Warning($"[WebRegister] 向 Portal (:{portalPort}) 注册超时未成功——Portal 可能未安装或启动过慢，门户里将看不到本工具。");
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
            logger.Error($"[WebRegister] Portal sidecar 请求异常: {ex.Message}");
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

        // 通过 → 签发 WebRegister 自己的 admin token（IAdminTokenService 与控制器共享同一鉴权域），
        // 经 URL fragment 注入 admin 页（fragment 不进服务器日志、不发往服务端），admin JS 读取后存入 sessionStorage。
        var adminToken = adminTokenService.IssueAdminToken();

        var adminPath = !string.IsNullOrEmpty(ret) && ret.StartsWith("/register/", StringComparison.Ordinal)
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
