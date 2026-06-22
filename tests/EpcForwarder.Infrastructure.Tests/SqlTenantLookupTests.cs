// tests/EpcForwarder.Infrastructure.Tests/SqlTenantLookupTests.cs
using Dapper;
using EpcForwarder.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EpcForwarder.Infrastructure.Tests;

[Collection("sql")]
public sealed class SqlTenantLookupTests(SqlServerFixture fx)
{
    [Fact]
    public void ResolveId_ReturnsTenantId_ForKnownCode()
    {
        var code = $"acme-{Guid.NewGuid():N}";
        int expected;
        using (var conn = new SqlConnection(fx.ConnectionString))
        {
            expected = conn.QuerySingle<int>(
                "INSERT INTO dbo.tenant(code,name) OUTPUT INSERTED.tenant_id VALUES(@c,'Acme')",
                new { c = code });
        }

        var sut = new SqlTenantLookup(new SqlConnectionFactory(fx.ConnectionString));

        Assert.Equal(expected, sut.ResolveId(code));
    }

    [Fact]
    public void ResolveId_ReturnsNull_ForUnknownCode()
    {
        var sut = new SqlTenantLookup(new SqlConnectionFactory(fx.ConnectionString));
        Assert.Null(sut.ResolveId($"missing-{Guid.NewGuid():N}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveId_ReturnsNull_WithoutDbRoundTrip_ForBlankCode(string code)
    {
        // 接続不能な接続文字列。DBに行こうとすれば例外になるので、
        // null が返る = 空白ガードで早期 return している(DB往復なし)契約を確認。
        var sut = new SqlTenantLookup(new SqlConnectionFactory("Server=invalid;Database=none;Connect Timeout=1"));

        Assert.Null(sut.ResolveId(code));
    }
}
