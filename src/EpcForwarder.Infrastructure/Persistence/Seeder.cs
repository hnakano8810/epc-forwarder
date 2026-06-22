// src/EpcForwarder.Infrastructure/Persistence/Seeder.cs
using Microsoft.Data.SqlClient;

namespace EpcForwarder.Infrastructure.Persistence;

/// <summary>
/// 実機E2E/デモ用のシードを冪等に投入する。tenant(1)/destination(test-hook)/product(ITEM-AAA)。
/// 列定義は db/migrations/0001_initial.sql・0002_destinations.sql を正とする。
/// </summary>
public static class Seeder
{
    public static void Apply(string connectionString, string webhookUrl)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SeedSql;
        var p = cmd.CreateParameter();
        p.ParameterName = "@url";
        p.Value = webhookUrl;
        cmd.Parameters.Add(p);
        cmd.ExecuteNonQuery();
    }

    private const string SeedSql = @"
SET XACT_ABORT ON;
BEGIN TRAN;

-- tenant_id=1 を固定(IDENTITY のため IDENTITY_INSERT + MERGE)
-- tenant は固定のデモ定数(code/name)のため insert-only。再実行では何もしない=意図的に WHEN MATCHED なし。
-- status は NOT NULL DEFAULT 'active' のため INSERT 列から省略。
SET IDENTITY_INSERT dbo.tenant ON;
MERGE dbo.tenant AS t
USING (SELECT 1 AS tenant_id, 'acme' AS code, 'Acme' AS name) AS s
ON (t.tenant_id = s.tenant_id)
WHEN NOT MATCHED THEN
  INSERT (tenant_id, code, name) VALUES (s.tenant_id, s.code, s.name);
SET IDENTITY_INSERT dbo.tenant OFF;

-- destination は (tenant_id, name) で一意扱い。
-- url は作業者ごとに変わる可変入力のため、再実行で WHEN MATCHED により更新する(tenant/product と非対称なのは意図的)。
MERGE dbo.destination AS d
USING (SELECT 1 AS tenant_id, 'test-hook' AS name) AS s
ON (d.tenant_id = s.tenant_id AND d.name = s.name)
WHEN MATCHED THEN
  UPDATE SET url = @url, http_method = 'POST', schema_version = '1',
             hmac_enabled = 0, allow_provisional = 1, is_active = 1
WHEN NOT MATCHED THEN
  INSERT (tenant_id, name, url, http_method, schema_version, hmac_enabled, allow_provisional, is_active)
  VALUES (s.tenant_id, s.name, @url, 'POST', '1', 0, 1, 1);

-- product は PK (tenant_id, search_key)。EPC 302DB42318A0038000001231 のマスク後検索キー。
-- product も固定のデモ定数(sku/search_key)のため insert-only。再実行では何もしない=意図的に WHEN MATCHED なし。
MERGE dbo.product AS p
USING (SELECT 1 AS tenant_id, CONVERT(varbinary(32), 0x302DB42318A0038000000000) AS search_key, 'ITEM-AAA' AS sku) AS s
ON (p.tenant_id = s.tenant_id AND p.search_key = s.search_key)
WHEN NOT MATCHED THEN
  INSERT (tenant_id, search_key, sku, item_code, color, size, description)
  VALUES (s.tenant_id, s.search_key, s.sku, 'ST-100', 'BLK', 'M', 'Sample');

COMMIT;
";
}
