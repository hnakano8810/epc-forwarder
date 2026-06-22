// tests/EpcForwarder.Infrastructure.Tests/SeederTests.cs
using Dapper;
using EpcForwarder.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EpcForwarder.Infrastructure.Tests;

[Collection("sql")]
public sealed class SeederTests(SqlServerFixture fx)
{
    private const string Url = "https://example.test/hook";

    [Fact]
    public void Seed_IsRepeatable_DoesNotDuplicateRows()
    {
        // 1回目
        Seeder.Apply(fx.ConnectionString, Url);
        var afterFirst = Counts();

        // 2回目(再実行安全)
        Seeder.Apply(fx.ConnectionString, Url);
        var afterSecond = Counts();

        Assert.Equal(afterFirst, afterSecond);                 // 増えない
        Assert.True(afterFirst.tenant >= 1);                   // 行は存在
        Assert.True(afterFirst.destination >= 1);
        Assert.True(afterFirst.product >= 1);
    }

    [Fact]
    public void Seed_UpdatesDestinationUrl_OnRerun()
    {
        Seeder.Apply(fx.ConnectionString, "https://old.test/hook");
        Seeder.Apply(fx.ConnectionString, "https://new.test/hook");

        using var conn = new SqlConnection(fx.ConnectionString);
        var url = conn.QuerySingle<string>(
            "SELECT url FROM dbo.destination WHERE tenant_id=1 AND name='test-hook'");
        Assert.Equal("https://new.test/hook", url);
    }

    private (int tenant, int destination, int product) Counts()
    {
        using var conn = new SqlConnection(fx.ConnectionString);
        var t = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.tenant WHERE tenant_id=1");
        var d = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.destination WHERE tenant_id=1 AND name='test-hook'");
        var p = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.product WHERE tenant_id=1 AND search_key=CONVERT(varbinary(32), 0x302DB42318A0038000000000)");
        return (t, d, p);
    }
}
