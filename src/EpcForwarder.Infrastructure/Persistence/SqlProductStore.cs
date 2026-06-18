// src/EpcForwarder.Infrastructure/Persistence/SqlProductStore.cs
using Dapper;
using EpcForwarder.Core.Abstractions;

namespace EpcForwarder.Infrastructure.Persistence;

public sealed class SqlProductStore(SqlConnectionFactory factory) : IProductCatalog, IProductWriteStore
{
    public string? ResolveSku(int tenantId, string searchKey)
    {
        using var conn = factory.Create();
        return conn.QuerySingleOrDefault<string>(
            "SELECT sku FROM dbo.product WHERE tenant_id = @tenantId AND search_key = @key",
            new { tenantId, key = Convert.FromHexString(searchKey) });
    }

    public void Upsert(ProductRecord product)
    {
        using var conn = factory.Create();
        conn.Execute(
            """
            -- add WITH (HOLDLOCK) if concurrent product upserts (bulk import) are introduced
            MERGE dbo.product AS t
            USING (SELECT @TenantId AS tenant_id, @Key AS search_key) AS s
               ON t.tenant_id = s.tenant_id AND t.search_key = s.search_key
            WHEN MATCHED THEN UPDATE SET
               sku = @Sku, item_code = @ItemCode, color = @Color, size = @Size,
               description = @Description, updated_at = SYSDATETIMEOFFSET()
            WHEN NOT MATCHED THEN INSERT
               (tenant_id, search_key, sku, item_code, color, size, description)
               VALUES (@TenantId, @Key, @Sku, @ItemCode, @Color, @Size, @Description);
            """,
            new
            {
                product.TenantId,
                Key = Convert.FromHexString(product.SearchKey),
                product.Sku,
                product.ItemCode,
                product.Color,
                product.Size,
                product.Description,
            });
    }
}
