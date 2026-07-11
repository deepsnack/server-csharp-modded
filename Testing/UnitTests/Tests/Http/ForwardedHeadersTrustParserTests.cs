using System.Net;
using NUnit.Framework;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Spt.Config;

namespace UnitTests.Tests.Http;

[TestFixture]
public class ForwardedHeadersTrustParserTests
{
    [Test]
    public void Parse_UsesSecureLoopbackDefaults()
    {
        var trust = ForwardedHeadersTrustParser.Parse(new ForwardedHeadersConfig());

        Assert.That(trust.ForwardLimit, Is.EqualTo(1));
        Assert.That(trust.KnownProxies, Does.Contain(IPAddress.Loopback));
        Assert.That(trust.KnownProxies, Does.Contain(IPAddress.IPv6Loopback));
        Assert.That(trust.KnownNetworks, Is.Empty);
    }

    [Test]
    public void Parse_RejectsInvalidTrustedProxy()
    {
        var config = new ForwardedHeadersConfig { KnownProxies = ["not-an-ip"] };

        Assert.Throws<FormatException>(() => ForwardedHeadersTrustParser.Parse(config));
    }

    [Test]
    public void Parse_RejectsInvalidForwardLimit()
    {
        var config = new ForwardedHeadersConfig { ForwardLimit = 0 };

        Assert.Throws<FormatException>(() => ForwardedHeadersTrustParser.Parse(config));
    }

    [Test]
    public void Parse_ParsesConfiguredNetwork()
    {
        var config = new ForwardedHeadersConfig { KnownNetworks = ["10.20.0.0/16"] };

        var trust = ForwardedHeadersTrustParser.Parse(config);

        Assert.That(trust.KnownNetworks, Has.Count.EqualTo(1));
        Assert.That(trust.KnownNetworks[0].BaseAddress, Is.EqualTo(IPAddress.Parse("10.20.0.0")));
        Assert.That(trust.KnownNetworks[0].PrefixLength, Is.EqualTo(16));
    }
}
