using DbUp;
using DbUp.Engine;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CapeCoast.Infrastructure.Migrations;

public static class Migrator
{
    // Scripts that can't run inside a transaction (timescale continuous aggregates)
    private static readonly string[] NoTransaction = { "0003_continuous_aggregates.sql", "0006_realtime_aggregates.sql", "0010_weather_daily.sql" };

    /// <summary>
    /// A freshly created Postgres container briefly accepts and then drops connections while it initialises,
    /// so wait for the server to be reachable before touching the schema.
    /// </summary>
    public static void WaitForServer(string connectionString, ILogger logger, int attempts, TimeSpan delay)
    {
        var master = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres", Timeout = 5 }.ConnectionString;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var conn = new NpgsqlConnection(master);
                conn.Open();
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or System.Net.Sockets.SocketException && attempt < attempts)
            {
                logger.LogInformation("Database not ready yet (attempt {Attempt} of {Attempts}), waiting", attempt, attempts);
                Thread.Sleep(delay);
            }
        }
    }

    public static bool Run(string connectionString, ILogger logger, int connectAttempts = 30, TimeSpan? connectDelay = null)
    {
        WaitForServer(connectionString, logger, connectAttempts, connectDelay ?? TimeSpan.FromSeconds(2));

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
