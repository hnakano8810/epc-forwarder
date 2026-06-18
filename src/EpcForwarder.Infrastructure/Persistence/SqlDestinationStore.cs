// src/EpcForwarder.Infrastructure/Persistence/SqlDestinationStore.cs
using Dapper;
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Delivery;

namespace EpcForwarder.Infrastructure.Persistence;

public sealed class SqlDestinationStore(SqlConnectionFactory factory) : IDestinationCatalog
{
    public IReadOnlyList<DeliveryTarget> GetActiveTargets(int tenantId)
    {
        using var conn = factory.Create();
        var destinations = conn.Query<DestinationRow>(
            """
            SELECT destination_id AS Id, url AS Url, http_method AS Method, schema_version AS SchemaVersion,
                   hmac_enabled AS HmacEnabled, hmac_secret_ref AS HmacSecretRef
            FROM dbo.destination WHERE tenant_id = @tenantId AND is_active = 1
            """, new { tenantId }).ToList();

        var targets = new List<DeliveryTarget>(destinations.Count);
        foreach (var d in destinations)
        {
            var headers = conn.Query<HeaderRow>(
                "SELECT header_name AS Name, value_ref AS ValueRef FROM dbo.destination_header WHERE destination_id = @id",
                new { id = d.Id })
                .ToDictionary(h => h.Name, h => h.ValueRef);

            targets.Add(new DeliveryTarget(d.Url, d.Method, d.SchemaVersion, d.HmacEnabled, d.HmacSecretRef, headers));
        }

        return targets;
    }

    private sealed class DestinationRow
    {
        public int Id { get; init; }
        public string Url { get; init; } = "";
        public string Method { get; init; } = "";
        public string SchemaVersion { get; init; } = "";
        public bool HmacEnabled { get; init; }
        public string? HmacSecretRef { get; init; }
    }

    private sealed class HeaderRow
    {
        public string Name { get; init; } = "";
        public string ValueRef { get; init; } = "";
    }
}
