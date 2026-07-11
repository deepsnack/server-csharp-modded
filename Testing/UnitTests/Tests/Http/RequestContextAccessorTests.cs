using System.Net;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using SPTarkov.Server.Core.Servers.Http;

namespace UnitTests.Tests.Http;

[TestFixture]
public class RequestContextAccessorTests
{
    [Test]
    public void BeginScope_NormalizesMappedIpv4_AndClearsAfterDispose()
    {
        var accessor = new RequestContextAccessor();
        var context = CreateContext("::ffff:203.0.113.9", "request-a");

        using (accessor.BeginScope(context))
        {
            Assert.That(accessor.Current?.RemoteIpAddress, Is.EqualTo(IPAddress.Parse("203.0.113.9")));
            Assert.That(accessor.Current?.TraceIdentifier, Is.EqualTo("request-a"));
        }

        Assert.That(accessor.Current, Is.Null);
    }

    [Test]
    public void BeginScope_RestoresNestedScope()
    {
        var accessor = new RequestContextAccessor();

        using (accessor.BeginScope(CreateContext("203.0.113.1", "outer")))
        {
            using (accessor.BeginScope(CreateContext("203.0.113.2", "inner")))
            {
                Assert.That(accessor.Current?.TraceIdentifier, Is.EqualTo("inner"));
            }

            Assert.That(accessor.Current?.TraceIdentifier, Is.EqualTo("outer"));
        }
    }

    [Test]
    public async Task BeginScope_IsIsolatedAcrossConcurrentAsyncFlows()
    {
        var accessor = new RequestContextAccessor();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<string?> Capture(string address, string traceIdentifier)
        {
            using var scope = accessor.BeginScope(CreateContext(address, traceIdentifier));
            await gate.Task;
            await Task.Yield();
            return accessor.Current?.TraceIdentifier;
        }

        var first = Task.Run(() => Capture("203.0.113.10", "first"));
        var second = Task.Run(() => Capture("203.0.113.11", "second"));
        gate.SetResult();

        Assert.That(await first, Is.EqualTo("first"));
        Assert.That(await second, Is.EqualTo("second"));
        Assert.That(accessor.Current, Is.Null);
    }

    private static DefaultHttpContext CreateContext(string address, string traceIdentifier)
    {
        var context = new DefaultHttpContext { TraceIdentifier = traceIdentifier };
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        return context;
    }
}
