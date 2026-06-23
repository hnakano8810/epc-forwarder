// tests/EpcForwarder.Infrastructure.Tests/VerifierTests.cs
using Dapper;
using EpcForwarder.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EpcForwarder.Infrastructure.Tests;

[Collection("sql")]
public sealed class VerifierTests(SqlServerFixture fx)
{
    /// <summary>session 行(指定 status)と reading 行 readingCount 件を直接投入し、tenant/session を返す。</summary>
    private (int tenant, Guid sid) Seed(string status, int readingCount)
    {
        var tenant = fx.NewTenant();
        var sid = Guid.NewGuid();
        using var conn = new SqlConnection(fx.ConnectionString);
        conn.Execute(
            """
            INSERT INTO dbo.session(public_id, tenant_id, type, status, created_at, last_event_at)
            VALUES(@sid, @tenant, 'shipment', @status, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET())
            """, new { sid, tenant, status });
        for (var i = 0; i < readingCount; i++)
        {
            conn.Execute(
                "INSERT INTO dbo.reading(session_id, tenant_id, epc, read_at) VALUES(@sid, @tenant, @epc, SYSDATETIMEOFFSET())",
                new { sid, tenant, epc = new byte[] { 0x30, 0x00, (byte)i } });
        }
        return (tenant, sid);
    }

    [Fact]
    public void Verify_ForwardedWithExpectedCount_Passes()
    {
        var (tenant, sid) = Seed("Forwarded", 3);
        Assert.Empty(Verifier.Verify(fx.ConnectionString, sid, tenant, 3));
    }

    [Fact]
    public void Verify_WrongCount_Fails()
    {
        var (tenant, sid) = Seed("Forwarded", 2);
        var failures = Verifier.Verify(fx.ConnectionString, sid, tenant, 3);
        Assert.Contains(failures, f => f.Contains("reading count"));
    }

    [Fact]
    public void Verify_NotForwarded_Fails()
    {
        var (tenant, sid) = Seed("Open", 1);
        var failures = Verifier.Verify(fx.ConnectionString, sid, tenant, 1);
        Assert.Contains(failures, f => f.Contains("status"));
    }

    [Fact]
    public void Verify_MissingSession_Fails()
    {
        var failures = Verifier.Verify(fx.ConnectionString, Guid.NewGuid(), fx.NewTenant(), 1);
        Assert.Contains(failures, f => f.Contains("not found"));
    }
}
