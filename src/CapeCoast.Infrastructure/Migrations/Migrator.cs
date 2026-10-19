using DbUp;
using DbUp.Engine;
using Microsoft.Extensions.Logging;

namespace CapeCoast.Infrastructure.Migrations;

public static class Migrator
{
    // Scripts that can't run inside a transaction (timescale continuous aggregates)
    private static readonly string[] NoTransaction = { "0003_continuous_aggregates.sql", "0006_realtime_aggregates.sql" };

    public static bool Run(string connectionString, ILogger logger)
    {
        var assembly = typeof(Migrator).Assembly;
        var transactional = new List<SqlScript>();
        var plain = new List<SqlScript>();

        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".sql")).OrderBy(n => n))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var script = new SqlScript(name, reader.ReadToEnd());
            (NoTransaction.Any(name.EndsWith) ? plain : transactional).Add(script);
        }

        EnsureDatabase.For.PostgresqlDatabase(connectionString);

        // One upgrader per script keeps ordering simple and lets us choose the transaction mode.
        var all = transactional.Concat(plain).OrderBy(s => s.Name).ToList();
        foreach (var script in all)
        {
            var builder = DeployChanges.To.PostgresqlDatabase(connectionString)
                .WithScript(script)
                .LogToNowhere();

            if (!NoTransaction.Any(script.Name.EndsWith))
                builder = builder.WithTransaction();

            var result = builder.Build().PerformUpgrade();
            if (!result.Successful)
            {
                logger.LogError(result.Error, "Migration {Script} failed", script.Name);
                return false;
            }
            if (result.Scripts.Any())
                logger.LogInformation("Applied migration {Script}", script.Name);
        }
        return true;
    }
}
