using System.Net;
using EpcForwarder.Core.Abstractions;

namespace EpcForwarder.Infrastructure.Net;

/// <summary>System.Net.Dns によるホスト解決。IPリテラルはそのまま返る。</summary>
public sealed class DnsHostResolver : IHostResolver
{
    public IReadOnlyList<IPAddress> Resolve(string host) => Dns.GetHostAddresses(host);
}
