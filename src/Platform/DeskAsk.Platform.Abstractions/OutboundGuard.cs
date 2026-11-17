using System.Net;
using System.Net.Sockets;

namespace DeskAsk.Platform;

/// <summary>
/// Keeps addresses that an administrator types (web hooks, Teams) from being used to reach the server's own network:
/// loopback, private, link-local (cloud metadata at 169.254.169.254), carrier-grade and multicast addresses are refused when the connection is made,
/// after the name has been resolved, so a name that points at a private address is refused too.
/// A site that really needs an internal receiver sets DESKASK_ALLOW_PRIVATE_WEBHOOKS=1 (the tests do too).
/// </summary>
public static class OutboundGuard
{
    public static bool AllowPrivate { get; set; } = Environment.GetEnvironmentVariable("DESKASK_ALLOW_PRIVATE_WEBHOOKS") == "1";

    public static bool IsPublic(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any) || a.Equals(IPAddress.None)) return false;
        if (a.AddressFamily == AddressFamily.InterNetworkV6)
            return !(a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast || (a.GetAddressBytes()[0] & 0xFE) == 0xFC);   // fe80::/10, fec0::/10, ff00::/8, fc00::/7
        var b = a.GetAddressBytes();
        return !(b[0] == 10 || b[0] == 127 || b[0] == 0 || b[0] >= 224
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || (b[0] == 192 && b[1] == 0 && b[2] == 0) || (b[0] == 198 && (b[1] == 18 || b[1] == 19)));
    }

    /// <summary>A handler whose connections only go to public addresses.</summary>
    public static SocketsHttpHandler Handler() => new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AllowAutoRedirect = false,
        ConnectCallback = async (ctx, ct) =>
        {
            var addrs = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            if (!AllowPrivate && (addrs.Length == 0 || addrs.Any(x => !IsPublic(x))))
                throw new HttpRequestException("This address points inside a private network, which web hooks may not reach.");
            var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { await s.ConnectAsync(addrs, ctx.DnsEndPoint.Port, ct); return new NetworkStream(s, ownsSocket: true); }
            catch { s.Dispose(); throw; }
        },
    };
}
