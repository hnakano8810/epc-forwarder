using System.Collections.Concurrent;
using EpcForwarder.Core.Abstractions;

namespace EpcForwarder.Infrastructure.Secrets;

/// <summary>内側の ISecretStore を TTL でキャッシュする。非nullのみキャッシュ(nullは毎回再取得)。</summary>
public sealed class CachingSecretStore(ISecretStore inner, IClock clock, TimeSpan ttl) : ISecretStore
{
    private readonly ConcurrentDictionary<string, (string Value, DateTimeOffset ExpiresAt)> _cache = new();

    public async Task<string?> GetAsync(string name, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(name, out var entry) && clock.UtcNow < entry.ExpiresAt)
        {
            return entry.Value;
        }

        // Concurrent misses are benign: both callers fetch and the last write wins.
        var fetchedAt = clock.UtcNow;
        var value = await inner.GetAsync(name, ct);
        if (value is not null)
        {
            _cache[name] = (value, fetchedAt.Add(ttl));
        }

        return value;
    }
}
