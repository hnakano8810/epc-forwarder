// tools/EpcForwarder.Migrate/Program.cs
using EpcForwarder.Infrastructure.Persistence;

static string Require(string name)
{
    var v = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(v))
    {
        Console.Error.WriteLine($"ERROR: 環境変数 {name} が未設定です。");
        Environment.Exit(2);
    }
    return v!;
}

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: EpcForwarder.Migrate <migrate|seed>");
    return 2;
}

try
{
    switch (args[0])
    {
        case "migrate":
            MigrationRunner.Apply(Require("EPCF_SQL_CONNECTION"));
            Console.WriteLine("migrate: done");
            return 0;
        case "seed":
            Seeder.Apply(Require("EPCF_SQL_CONNECTION"), Require("SEED_WEBHOOK_URL"));
            Console.WriteLine("seed: done");
            return 0;
        default:
            Console.Error.WriteLine($"unknown command: {args[0]}");
            return 2;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAILED: {ex.Message}");
    return 1;
}
