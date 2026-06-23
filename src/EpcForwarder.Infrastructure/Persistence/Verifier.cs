// src/EpcForwarder.Infrastructure/Persistence/Verifier.cs
using Dapper;
using Microsoft.Data.SqlClient;

namespace EpcForwarder.Infrastructure.Persistence;

/// <summary>
/// デプロイ E2E の SQL 直接合否判定。クエリ API(認証必須)に依存せず、取込→配信到達を DB で確認する。
/// 失敗理由の一覧を返す(空＝合格)。例外は投げず呼び出し側が exit code を決める。
/// </summary>
public static class Verifier
{
    public static IReadOnlyList<string> Verify(string connectionString, Guid sessionId, int tenantId, int expectedCount)
    {
        var failures = new List<string>();
        using var conn = new SqlConnection(connectionString);

        var readingCount = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.reading WHERE session_id = @sessionId AND tenant_id = @tenantId AND excluded = 0",
            new { sessionId, tenantId });
        if (readingCount != expectedCount)
        {
            failures.Add($"reading count {readingCount} != expected {expectedCount} (session={sessionId}, tenant={tenantId})");
        }

        var status = conn.QuerySingleOrDefault<string>(
            "SELECT status FROM dbo.session WHERE public_id = @sessionId AND tenant_id = @tenantId",
            new { sessionId, tenantId });
        if (status is null)
        {
            failures.Add($"session not found (session={sessionId}, tenant={tenantId})");
        }
        else if (!string.Equals(status, "Forwarded", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"session status '{status}' != Forwarded");
        }

        return failures;
    }
}
