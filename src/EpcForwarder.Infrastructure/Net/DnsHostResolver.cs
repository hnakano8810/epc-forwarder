using System.Net;
using EpcForwarder.Core.Abstractions;

namespace EpcForwarder.Infrastructure.Net;

/// <summary>System.Net.Dns によるホスト解決。IPリテラルはそのまま返る。</summary>
public sealed class DnsHostResolver : IHostResolver
{
    // Synchronous by design (IHostResolver is sync, used by the SSRF guard before send).
    public IReadOnlyList<IPAddress> Resolve(string host) => Dns.GetHostAddresses(host);
}
