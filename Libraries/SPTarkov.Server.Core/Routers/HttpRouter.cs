using System.Collections.Frozen;
using Microsoft.AspNetCore.Http;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;

namespace SPTarkov.Server.Core.Routers;

[Injectable]
public class HttpRouter(IEnumerable<StaticRouter> staticRouters, IEnumerable<DynamicRouter> dynamicRoutes)
{
    // O(1) 路由索引（原 SPT-Performance FastRouter 内联）：静态路由 FrozenDictionary 精确匹配，
    // 动态路由按 router 分组子串列表。首次使用时懒构建（此时 DI 中已含全部 mod 路由）。
    // 语义与基线线性扫描完全一致：同一 url 的多个静态 router 依注册序链式传递 output；
    // 动态阶段每个命中的 router 只执行一次。
    protected FrozenDictionary<string, StaticRouter[]>? staticIndex;
    protected (DynamicRouter router, string[] substrings)[]? dynamicIndex;
    protected readonly object indexGate = new();

    protected void EnsureIndex()
    {
        if (staticIndex is not null)
        {
            return;
        }

        lock (indexGate)
        {
            if (staticIndex is not null)
            {
                return;
            }

            var staticMap = new Dictionary<string, List<StaticRouter>>(StringComparer.Ordinal);
            foreach (var router in staticRouters)
            {
                foreach (var route in router.GetInternalHandledRoutes())
                {
                    if (route.dynamic)
                    {
                        continue;
                    }

                    if (!staticMap.TryGetValue(route.route, out var list))
                    {
                        list = [];
                        staticMap[route.route] = list;
                    }

                    if (!list.Contains(router))
                    {
                        list.Add(router);
                    }
                }
            }

            dynamicIndex = dynamicRoutes
                .Select(router =>
                    (router, router.GetInternalHandledRoutes().Where(r => r.dynamic).Select(r => r.route).ToArray())
                )
                .ToArray();

            staticIndex = staticMap.ToFrozenDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal);
        }
    }

    public bool CanHandle(HttpContext context)
    {
        EnsureIndex();
        var url = context.Request.Path.Value;
        if (url is null)
        {
            return false;
        }

        return staticIndex!.ContainsKey(url) || dynamicIndex!.Any(d => d.substrings.Any(url.Contains));
    }

    public async ValueTask<string?> GetResponse(HttpRequest req, MongoId sessionID, string? body)
    {
        EnsureIndex();

        var url = req.Path.Value;

        // remove retry from url
        if (url?.Contains("?retry=") ?? false)
        {
            url = url.Split("?retry=")[0];
        }

        if (url is null)
        {
            return string.Empty;
        }

        string? output = "";

        if (staticIndex!.TryGetValue(url, out var matchedStaticRouters))
        {
            foreach (var router in matchedStaticRouters)
            {
                output = await router.HandleStatic(url, body, sessionID, output ?? string.Empty) as string;
            }

            return output;
        }

        foreach (var (router, substrings) in dynamicIndex!)
        {
            if (substrings.Any(url.Contains))
            {
                output = await router.HandleDynamic(url, body, sessionID, output ?? string.Empty) as string;
            }
        }

        return output;
    }
}
