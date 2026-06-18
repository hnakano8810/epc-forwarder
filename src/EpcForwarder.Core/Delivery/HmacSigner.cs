using System.Security.Cryptography;
using System.Text;

namespace EpcForwarder.Core.Delivery;

/// <summary>HMAC-SHA256(secret, timestamp + "." + body)。詳細は docs/design/webhook-contract.md §4。</summary>
public static class HmacSigner
{
    public static string Sign(string secret, string timestamp, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
