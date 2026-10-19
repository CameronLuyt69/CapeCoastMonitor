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

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var cs = _container.GetConnectionString();
        Assert.True(Migrator.Run(cs, NullLogger.Instance), "migrations failed");
        Db = NpgsqlDataSource.Create(cs);
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
        return await conn.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table}");
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
}
