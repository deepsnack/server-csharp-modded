using System.Net;
using Microsoft.AspNetCore.Http;
using SPTarkov.DI.Annotations;

namespace SPTarkov.Server.Core.Servers.Http;

/// <summary>
/// Immutable snapshot of the HTTP connection that is safe to consume below the router layer.
/// </summary>
public sealed record SptRequestContext(IPAddress? RemoteIpAddress, string TraceIdentifier)
{
    public static SptRequestContext FromHttpContext(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address?.IsIPv4MappedToIPv6 == true)
        {
            address = address.MapToIPv4();
        }

        return new SptRequestContext(address, context.TraceIdentifier);
    }
}

/// <summary>
/// Read-only ambient request context. Consumers cannot replace the current snapshot.
/// </summary>
public interface IRequestContextAccessor
{
    SptRequestContext? Current { get; }
}

[Injectable(InjectionType.Singleton)]
public sealed class RequestContextAccessor : IRequestContextAccessor
{
    private static readonly AsyncLocal<ScopeState?> Ambient = new();

    public SptRequestContext? Current => Ambient.Value?.Context;

    /// <summary>
    /// Starts an async-flow-local request scope and restores the previous scope on disposal.
    /// </summary>
    public IDisposable BeginScope(HttpContext context)
    {
        var previous = Ambient.Value;
        Ambient.Value = new ScopeState(SptRequestContext.FromHttpContext(context));
        return new Scope(previous);
    }

    private sealed record ScopeState(SptRequestContext Context);

    private sealed class Scope(ScopeState? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Ambient.Value = previous;
            _disposed = true;
        }
    }
}
