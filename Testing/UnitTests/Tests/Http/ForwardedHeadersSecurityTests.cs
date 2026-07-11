using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Spt.Config;

namespace UnitTests.Tests.Http;

[TestFixture]
public class ForwardedHeadersSecurityTests
{
    [Test]
    public async Task TrustedProxy_CanSupplyForwardedAddress()
    {
        var observed = await Execute(IPAddress.Loopback, "198.51.100.42");

        Assert.That(observed, Is.EqualTo(IPAddress.Parse("198.51.100.42")));
    }

    [Test]
    public async Task UntrustedClient_CannotSpoofForwardedAddress()
    {
        var directAddress = IPAddress.Parse("203.0.113.55");
        var observed = await Execute(directAddress, "198.51.100.42");

        Assert.That(observed, Is.EqualTo(directAddress));
    }

    private static async Task<IPAddress?> Execute(IPAddress directAddress, string forwardedFor)
    {
        var trust = ForwardedHeadersTrustParser.Parse(new ForwardedHeadersConfig());
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor,
            ForwardLimit = trust.ForwardLimit,
        };
        options.KnownProxies.Clear();
        options.KnownNetworks.Clear();
        foreach (var proxy in trust.KnownProxies) options.KnownProxies.Add(proxy);

        IPAddress? observed = null;
        var builder = new ApplicationBuilder(new ServiceCollection().AddLogging().BuildServiceProvider());
        builder.UseForwardedHeaders(options);
        builder.Run(context =>
        {
            observed = context.Connection.RemoteIpAddress;
            return Task.CompletedTask;
        });

        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = directAddress;
        httpContext.Request.Headers["X-Forwarded-For"] = forwardedFor;
        await builder.Build()(httpContext);
        return observed;
    }
}
