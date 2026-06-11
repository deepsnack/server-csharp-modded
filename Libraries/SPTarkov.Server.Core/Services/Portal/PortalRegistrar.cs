using System.Net.Http.Headers;
using System.Text.Json;

namespace SPTarkov.Server.Core.Services.Portal;

/// <summary>
/// sidecar 启动后向 SptManagerPortal 推送自我注册（POST /api/mods/register），让 Portal 动态发现本工具。
/// 注册失败（Portal 尚未启动等）时静默重试，直到成功或超出总时长预算 —— 不影响主服务器与注册功能。
/// </summary>
internal static class PortalRegistrar
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // 重试间隔与总时长预算。Portal 与子工具的 OnLoad 加载顺序不固定：子工具可能远早于 Portal 启动
    // （实测见过 ~26s 的间隔）。早期版本只重试 5×2s≈10s，窗口内 Portal 还没监听就永久放弃，导致
    // 该工具在门户里"扫不到"。Portal 注册表是内存态、仅靠 push，错过即不会再补登记。故这里把窗口拉长到
    // 数分钟、轮询直到成功，覆盖任意加载顺序与慢启动；Portal 真的没装时也只是这段时间内的廉价本地失败连接。
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TotalBudget = TimeSpan.FromMinutes(3);

    /// <summary>
    /// 向 Portal 注册本工具，轮询重试直到成功或超出 <see cref="TotalBudget"/>。
    /// </summary>
    /// <returns>true=注册成功；false=预算内始终未成功（Portal 未启动/未安装）。</returns>
    public static async Task<bool> RegisterAsync(string portalBaseUrl, PortalModRegistration reg)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(reg, JsonOptions);
        var deadline = DateTime.UtcNow + TotalBudget;
        while (true)
        {
            try
            {
                using var content = new ByteArrayContent(payload);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using var resp = await Http.PostAsync($"{portalBaseUrl}/api/mods/register", content);
                if (resp.IsSuccessStatusCode) return true;
            }
            catch { /* Portal 未就绪或网络异常，稍后重试 */ }
            if (DateTime.UtcNow + RetryInterval >= deadline) return false;
            await Task.Delay(RetryInterval);
        }
    }
}

/// <summary>注册请求体。序列化为 camelCase JSON，字段与 Portal 的 ModRegisterRequest 对齐。</summary>
internal record PortalModRegistration(
    string Id,
    string Name,
    string DisplayName,
    string Version,
    int Port,
    string Icon,
    string SsoEntry = "/api/sso",
    string DefaultReturn = "/",
    string ProbeHost = "127.0.0.1");
