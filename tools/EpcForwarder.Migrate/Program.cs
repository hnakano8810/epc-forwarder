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
    Console.Error.WriteLine("usage: EpcForwarder.Migrate <migrate|seed|verify>");
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
        case "verify":
            if (args.Length < 4)
            {
                Console.Error.WriteLine("usage: EpcForwarder.Migrate verify <session_id> <tenant_id> <expected_count>");
                return 2;
            }
            if (!Guid.TryParse(args[1], out var sessionId))
            {
                Console.Error.WriteLine($"ERROR: session_id が GUID ではありません: '{args[1]}'");
                return 2;
            }
            if (!int.TryParse(args[2], out var tenantId) || !int.TryParse(args[3], out var expectedCount))
            {
                Console.Error.WriteLine("ERROR: tenant_id / expected_count は整数で指定してください。");
                return 2;
            }
            var failures = Verifier.Verify(Require("EPCF_SQL_CONNECTION"), sessionId, tenantId, expectedCount);
            if (failures.Count > 0)
            {
                foreach (var f in failures)
                {
                    Console.Error.WriteLine($"VERIFY FAIL: {f}");
                }
                return 1;
            }
            Console.WriteLine($"verify: PASSED (session={sessionId}, tenant={tenantId}, readings={expectedCount}, forwarded)");
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
