using CapeCoast.Core.Domain;
using CapeCoast.Core.Sources;
using CapeCoast.Infrastructure.Data;
using CapeCoast.Infrastructure.Ingestion;
using CapeCoast.Infrastructure.Migrations;
using CapeCoast.Infrastructure.Sources;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace CapeCoast.Tests;

public class DatabaseFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("timescale/timescaledb-ha:pg16")
        .WithDatabase("capecoast_test")
        .Build();

    public NpgsqlDataSource Db { get; private set; } = null!;
    public string ConnectionString { get; private set; } = "";

    /// <summary>Each beach's bearing exactly as the migrations left it, before any test could change it.</summary>
    public Dictionary<string, (double? Deg, string Source)> InitialFacing { get; private set; } = new();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var cs = _container.GetConnectionString();
        ConnectionString = cs;
        Assert.True(Migrator.Run(cs, NullLogger.Instance), "migrations failed");
        Db = NpgsqlDataSource.Create(cs);

        await using var conn = await Db.OpenConnectionAsync();
        var rows = await conn.QueryAsync<(string Slug, double? Deg, string Source)>("SELECT slug, facing_deg::float8, facing_source FROM locations");
        InitialFacing = rows.ToDictionary(r => r.Slug, r => (r.Deg, r.Source));
    }

    /// <summary>Puts every beach's bearing back as the migrations set it, for tests that change them.</summary>
    public async Task RestoreFacingAsync()
    {
        await using var conn = await Db.OpenConnectionAsync();
        foreach (var (slug, (deg, source)) in InitialFacing)
            await conn.ExecuteAsync("UPDATE locations SET facing_deg = @deg, facing_source = @source WHERE slug = @slug", new { slug, deg, source });
    }

    public async Task DisposeAsync()
    {
        await Db.DisposeAsync();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition("db")]
public class DbCollection : ICollectionFixture<DatabaseFixture> { }

[Collection("db")]
public class IngestionIntegrationTests
{
    private readonly DatabaseFixture _fx;

    public IngestionIntegrationTests(DatabaseFixture fx) => _fx = fx;

    private class RecordingHook : CapeCoast.Core.Alerts.IPostIngestHook
    {
        public List<string> Sources { get; } = new();
        public Task AfterRunAsync(string source, CancellationToken ct) { Sources.Add(source); return Task.CompletedTask; }
    }

    private IngestionService Service() => new(
        new LocationRepository(_fx.Db), new ObservationRepository(_fx.Db),
        new IngestionRunRepository(_fx.Db), NullLogger<IngestionService>.Instance);

    private class FixtureMarineSource : IMarineSource
    {
        public string Name => "fixture-marine";

        public Task<IReadOnlyList<MarineObservation>> FetchAsync(IReadOnlyList<Location> locations, CancellationToken ct)
        {
            var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "marine-muizenberg.json"));
            // reuse the single-location fixture for the first active beach only
            return Task.FromResult(OpenMeteoMarineSource.Parse(json, new[] { locations[0] }));
        }
    }

    private class BrokenWindSource : IWindSource
    {
        public string Name => "broken-wind";
        public Task<IReadOnlyList<WindObservation>> FetchAsync(IReadOnlyList<Location> l, CancellationToken ct) =>
            throw new HttpRequestException("upstream is down");
    }

    private async Task Reset()
    {
        await using var conn = await _fx.Db.OpenConnectionAsync();
        await conn.ExecuteAsync("DELETE FROM marine_observations WHERE source = 'fixture-marine'");
    }

    private async Task<long> Count(string table)
    {
        await using var conn = await _fx.Db.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table} WHERE source = 'fixture-marine'");
    }

    [Fact]
    public async Task Running_the_marine_job_twice_creates_no_duplicates()
    {
        await Reset();
        var service = Service();
        var source = new FixtureMarineSource();

        var first = await service.RunMarineAsync(source, CancellationToken.None);
        var rowsAfterFirst = await Count("marine_observations");
        var second = await service.RunMarineAsync(source, CancellationToken.None);
        var rowsAfterSecond = await Count("marine_observations");

        Assert.Equal(48, rowsAfterFirst);
        Assert.Equal(rowsAfterFirst, rowsAfterSecond);
        Assert.Equal(48, first.Written);
        Assert.Equal(0, second.Written);
    }

    [Fact]
    public async Task Changed_values_are_updated_in_place()
    {
        await Reset();
        var service = Service();
        await service.RunMarineAsync(new FixtureMarineSource(), CancellationToken.None);

        await using (var conn = await _fx.Db.OpenConnectionAsync())
            await conn.ExecuteAsync("UPDATE marine_observations SET wave_height_m = 9.9 WHERE source = 'fixture-marine'");

        var rerun = await service.RunMarineAsync(new FixtureMarineSource(), CancellationToken.None);

        Assert.Equal(48, rerun.Written);
        await using var check = await _fx.Db.OpenConnectionAsync();
        var max = await check.ExecuteScalarAsync<double>(
            "SELECT max(wave_height_m) FROM marine_observations WHERE source = 'fixture-marine'");
        Assert.True(max < 9.9);
    }

    [Fact]
    public async Task Every_run_is_recorded_and_a_failure_does_not_throw()
    {
        var service = Service();

        var result = await service.RunWindAsync(new BrokenWindSource(), CancellationToken.None);

        Assert.Equal("failed", result.Status);
        await using var conn = await _fx.Db.OpenConnectionAsync();
        var row = await conn.QuerySingleAsync<(string Status, string Error)>(
            "SELECT status, error_message FROM ingestion_runs WHERE id = @id", new { id = result.RunId });
        Assert.Equal("failed", row.Status);
        Assert.Contains("upstream is down", row.Error);
    }

    [Fact]
    public async Task Alert_evaluation_runs_after_rows_are_written_but_not_when_nothing_changed_or_it_failed()
    {
        await Reset();
        var hook = new RecordingHook();
        var service = new IngestionService(new LocationRepository(_fx.Db), new ObservationRepository(_fx.Db),
            new IngestionRunRepository(_fx.Db), NullLogger<IngestionService>.Instance, hook);

        await service.RunMarineAsync(new FixtureMarineSource(), CancellationToken.None);     // writes 48 rows
        await service.RunMarineAsync(new FixtureMarineSource(), CancellationToken.None);     // identical, writes 0
        await service.RunWindAsync(new BrokenWindSource(), CancellationToken.None);          // fails

        Assert.Equal(new[] { "fixture-marine" }, hook.Sources);
    }

    [Theory]
    [InlineData("succeeded", 10, true)]
    [InlineData("flagged", 10, true)]      // fresh rows, some values missing
    [InlineData("flagged", 0, false)]      // a report rejected as unreadable wrote nothing
    [InlineData("failed", 0, false)]
    public async Task Freshness_counts_a_run_as_fresh_only_when_it_delivered_data(string status, int written, bool fresh)
    {
        var runs = new IngestionRunRepository(_fx.Db);
        var source = $"freshness-{status}-{written}";
        var id = await runs.StartAsync(source, null);
        await runs.FinishAsync(id, status, 10, written, null);

        var result = await runs.GetStatusAsync(source);

        Assert.Equal(fresh, result.LastSuccessAt is not null);
        Assert.Equal(status, result.LastStatus);
    }

    [Fact]
    public void A_reachable_database_does_not_make_the_migrator_wait()
    {
        var waits = 0;
        var logger = new CountingLogger(() => waits++);

        Migrator.WaitForServer(_fx.ConnectionString, logger, attempts: 3, delay: TimeSpan.FromMilliseconds(1));

        Assert.Equal(0, waits);
    }
}
