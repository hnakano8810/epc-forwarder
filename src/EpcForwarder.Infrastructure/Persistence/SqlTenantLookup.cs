// src/EpcForwarder.Infrastructure/Persistence/SqlTenantLookup.cs
using System.Collections.Concurrent;
using Dapper;
using EpcForwarder.Core.Abstractions;

namespace EpcForwarder.Infrastructure.Persistence;

/// <summary>tenant.code → tenant_id を解決。code→id は不変なのでプロセス内メモ化する。</summary>
public sealed class SqlTenantLookup(SqlConnectionFactory factory) : ITenantLookup
{
    private readonly ConcurrentDictionary<string, int> _cache = new(StringComparer.Ordinal);

    public int? ResolveId(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        if (_cache.TryGetValue(code, out var cached))
        {
            return cached;
        }

        using var conn = factory.Create();
        var id = conn.QuerySingleOrDefault<int?>(
            "SELECT tenant_id FROM dbo.tenant WHERE code = @code",
            new { code });

        if (id is int value)
        {
            _cache[code] = value;
        }

        return id;
    }
}
