using System.Net;
using SPTarkov.Server.Core.Models.Spt.Config;

namespace SPTarkov.Server.Core.Helpers;

public sealed record ForwardedHeadersTrust(
    int ForwardLimit,
    IReadOnlyList<IPAddress> KnownProxies,
    IReadOnlyList<System.Net.IPNetwork> KnownNetworks
);

public static class ForwardedHeadersTrustParser
{
    public static ForwardedHeadersTrust Parse(ForwardedHeadersConfig config)
    {
        if (config.ForwardLimit < 1)
        {
            throw new FormatException("forwardedHeaders.forwardLimit must be at least 1.");
        }

        var proxies = config.KnownProxies.Select(ParseAddress).ToList();
        var networks = config.KnownNetworks.Select(ParseNetwork).ToList();
        return new ForwardedHeadersTrust(config.ForwardLimit, proxies, networks);
    }

    private static IPAddress ParseAddress(string value)
    {
        if (!IPAddress.TryParse(value, out var address))
        {
            throw new FormatException($"Invalid trusted proxy IP address in http.json: {value}");
        }

        return address;
    }

    private static System.Net.IPNetwork ParseNetwork(string value)
    {
        if (!System.Net.IPNetwork.TryParse(value, out var network))
        {
            throw new FormatException($"Invalid trusted proxy network in http.json: {value}");
        }

        return network;
    }
}
