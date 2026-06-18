// tests/EpcForwarder.Infrastructure.Tests/MigrationSmokeTests.cs
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EpcForwarder.Infrastructure.Tests;

[Collection("sql")]
public sealed class MigrationSmokeTests(SqlServerFixture fx)
{
    [Fact]
    public void Migration_CreatesExpectedTables()
    {
        using var conn = new SqlConnection(fx.ConnectionString);
        var tables = conn.Query<string>(
            "SELECT name FROM sys.tables WHERE name IN ('tenant','product','session','reading','snapshot','destination','destination_header','mask')")
            .ToHashSet();

        Assert.Contains("session", tables);
        Assert.Contains("reading", tables);
        Assert.Contains("snapshot", tables);
        Assert.Contains("product", tables);
        Assert.Contains("tenant", tables);
        Assert.Contains("destination", tables);
        Assert.Contains("destination_header", tables);
        Assert.Contains("mask", tables);
    }
}
