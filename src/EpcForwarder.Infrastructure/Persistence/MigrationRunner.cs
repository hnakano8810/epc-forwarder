// src/EpcForwarder.Infrastructure/Persistence/MigrationRunner.cs
using System.Reflection;
using Microsoft.Data.SqlClient;

namespace EpcForwarder.Infrastructure.Persistence;

/// <summary>埋め込まれた db/migrations/*.sql を名前順に適用する(冪等: スクリプトは IF NOT EXISTS で書く)。</summary>
public static class MigrationRunner
{
    public static void Apply(string connectionString)
    {
        var asm = typeof(MigrationRunner).Assembly;
        var names = asm.GetManifestResourceNames()
            .Where(n => n.StartsWith("EpcForwarder.Migrations.", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        using var connection = new SqlConnection(connectionString);
        connection.Open();
        foreach (var name in names)
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }
}
