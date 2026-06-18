using System.Net;
using System.Net.Sockets;

namespace EpcForwarder.Core.Delivery;

public interface IHostResolver
{
    IReadOnlyList<IPAddress> Resolve(string host);
}

public sealed record WebhookUrlGuardOptions(bool AllowHttp, bool AllowPrivateNetworks);

/// <summary>送信先URLのSSRFガード。詳細は docs/design/webhook-contract.md §7。</summary>
public static class WebhookUrlGuard
{
    public static void Validate(string url, WebhookUrlGuardOptions options, IHostResolver resolver)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"Invalid URL: {url}");
        }

        var isHttps = uri.Scheme == Uri.UriSchemeHttps;
        var isHttp = uri.Scheme == Uri.UriSchemeHttp;
        if (!isHttps && !(isHttp && options.AllowHttp))
        {
            throw new InvalidOperationException($"URL scheme not allowed: {uri.Scheme}");
        }

        if (options.AllowPrivateNetworks)
        {
            return;
        }

        foreach (var ip in resolver.Resolve(uri.Host))
        {
            if (IsBlocked(ip))
            {
                throw new InvalidOperationException($"Destination resolves to a blocked address: {ip}");
            }
        }
    }

    private static bool IsBlocked(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            // 10/8, 172.16/12, 192.168/16, 169.254/16
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 169 && b[1] == 254) return true;
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal) return true;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true; // fc00::/7 unique-local
            return false;
        }

        return false;
    }
}
