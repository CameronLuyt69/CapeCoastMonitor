using System.Text.Json;
using CapeCoast.Core.Domain;
using CapeCoast.Infrastructure.Data;
using CapeCoast.Infrastructure.Export;
using CapeCoast.Infrastructure.Presentation;
using CapeCoast.Infrastructure.Sources;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CapeCoast.Tests;

[Collection("db")]
public class StaticExporterTests : IDisposable
{
    private readonly DatabaseFixture _fx;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ccm-export-" + Guid.NewGuid().ToString("N"));
    private long _runId;

    public StaticExporterTests(DatabaseFixture fx) => _fx = fx;

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private StaticExporter Exporter(NpgsqlDataSource? db = null, bool includeWaterQuality = false)
    {
        db ??= _fx.Db;
        return new StaticExporter(new ReadRepository(db),
            new StatusService(new ReadRepository(db), new IngestionRunRepository(db), Options.Create(new SourcesOptions())),
            Options.Create(new ExportOptions { IncludeWaterQuality = includeWaterQuality }),
            new ConfigurationBuilder().Build(), NullLogger<StaticExporter>.Instance);
    }

    private async Task Seed()
    {
        await using var conn = await _fx.Db.OpenConnectionAsync();
        await conn.ExecuteAsync(@"DELETE FROM marine_observations; DELETE FROM wind_observations;
            DELETE FROM water_quality_samples; DELETE FROM source_documents; DELETE FROM ingestion_runs; UPDATE locations SET active = true");

        _runId = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO ingestion_runs (source, status, finished_at, rows_fetched, rows_written) VALUES ('open-meteo-marine', 'succeeded', now(), 5, 5) RETURNING id");
        var loc = await conn.ExecuteScalarAsync<int>("SELECT id FROM locations WHERE slug = 'clifton-4th'");
        var hour = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day, DateTime.UtcNow.Hour, 0, 0, DateTimeKind.Utc);

        for (var i = -5; i <= 0; i++)
            await conn.ExecuteAsync(@"INSERT INTO marine_observations (location_id, source, observed_at, forecast_issued_at, wave_height_m, ingestion_run_id)
                VALUES (@loc, 'open-meteo-marine', @t, now(), @v, @run)", new { loc, t = hour.AddHours(i), v = 1.0 + i * 0.1, run = _runId });

        var doc = await conn.ExecuteScalarAsync<long>(@"INSERT INTO source_documents (source, url, sha256, title)
            VALUES ('cct-coastal-water-quality', 'https://example.test/report.pdf', 'sha-export', 'Report') RETURNING id");
        await conn.ExecuteAsync(@"INSERT INTO water_quality_samples (location_id, sampled_at, enterococci_cfu_100ml, rating, source_document_id, ingestion_run_id)
            VALUES (@loc, now() - interval '3 days', 300, 'Amber', @doc, @run)", new { loc, doc, run = _runId });
    }

    private JsonElement Read(string relative) => JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, relative))).RootElement;

    [Fact]
    public async Task By_default_the_snapshot_leaves_the_Citys_results_out_and_says_so()
    {
        await Seed();

        var result = await Exporter().ExportAsync(_dir, CancellationToken.None);

        Assert.False(result.IncludedWaterQuality);
        var locations = Read("locations.geojson");
        Assert.False(locations.GetProperty("waterQualityIncluded").GetBoolean());
        Assert.All(locations.GetProperty("features").EnumerateArray(),
            f => Assert.Equal(JsonValueKind.Null, f.GetProperty("properties").GetProperty("waterQuality").ValueKind));
        Assert.Equal(JsonValueKind.Null, Read("beaches/clifton-4th.json").GetProperty("waterQuality").ValueKind);
        // the number must not be anywhere in the exported files, not just hidden from the map
        Assert.DoesNotContain("example.test/report.pdf", Directory.GetFiles(_dir, "*.*", SearchOption.AllDirectories).Select(File.ReadAllText).Aggregate("", (a, b) => a + b));
    }

    [Fact]
    public async Task Water_quality_can_be_included_on_purpose()
    {
        await Seed();

        var result = await Exporter(includeWaterQuality: true).ExportAsync(_dir, CancellationToken.None);

        Assert.True(result.IncludedWaterQuality);
        var locations = Read("locations.geojson");
        Assert.True(locations.GetProperty("waterQualityIncluded").GetBoolean());
        var clifton = locations.GetProperty("features").EnumerateArray().First(f => f.GetProperty("id").GetString() == "clifton-4th");
        Assert.Equal("exceeds", clifton.GetProperty("properties").GetProperty("waterQuality").GetProperty("band").GetString());
        var beach = Read("beaches/clifton-4th.json");
        Assert.Equal(300, beach.GetProperty("waterQuality").GetProperty("samples")[0].GetProperty("enterococciCfu100ml").GetInt32());
    }

    [Fact]
    public async Task The_cli_style_override_beats_the_configured_default()
    {
        await Seed();

        var result = await Exporter(includeWaterQuality: false).ExportAsync(_dir, CancellationToken.None, includeWaterQuality: true);

        Assert.True(result.IncludedWaterQuality);
    }

    [Fact]
    public async Task Every_beach_gets_a_file_with_every_metric_raw_and_daily()
    {
        await Seed();

        var result = await Exporter().ExportAsync(_dir, CancellationToken.None);

        Assert.Equal(10, result.Beaches);
        Assert.Equal(10, Directory.GetFiles(Path.Combine(_dir, "beaches"), "*.json").Length);
        var history = Read("beaches/clifton-4th.json").GetProperty("history");
        foreach (var metric in MetricCatalog.Series)
        {
            Assert.Equal("raw", history.GetProperty(metric.Key).GetProperty("raw").GetProperty("interval").GetString());
            Assert.Equal("day", history.GetProperty(metric.Key).GetProperty("day").GetProperty("interval").GetString());
        }
        Assert.Equal(6, history.GetProperty("wave_height").GetProperty("raw").GetProperty("points").GetArrayLength());
    }

    [Fact]
    public async Task The_runs_behind_the_points_are_in_the_snapshot_so_the_panel_can_still_trace_them()
    {
        await Seed();

        await Exporter().ExportAsync(_dir, CancellationToken.None);

        var runs = Read("beaches/clifton-4th.json").GetProperty("runs");
        Assert.Equal("open-meteo-marine", runs.GetProperty(_runId.ToString()).GetProperty("source").GetString());
    }

    [Fact]
    public async Task The_files_use_the_same_shapes_as_the_api()
    {
        await Seed();
        await Exporter().ExportAsync(_dir, CancellationToken.None);

        var exported = Read("locations.geojson");
        var rows = await new ReadRepository(_fx.Db).LatestConditionsAsync();
        var fromShapes = JsonSerializer.SerializeToElement(Shapes.Locations(rows, 240, false, DateTime.UtcNow), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(fromShapes.GetProperty("features").GetArrayLength(), exported.GetProperty("features").GetArrayLength());
        Assert.Equal(
            fromShapes.GetProperty("features")[0].GetProperty("properties").EnumerateObject().Select(p => p.Name),
            exported.GetProperty("features")[0].GetProperty("properties").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task A_second_export_replaces_the_first_and_leaves_no_stale_files_or_staging_folders()
    {
        await Seed();
        await Exporter().ExportAsync(_dir, CancellationToken.None);
        File.WriteAllText(Path.Combine(_dir, "beaches", "removed-beach.json"), "{}");

        await Exporter().ExportAsync(_dir, CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(_dir, "beaches", "removed-beach.json")));
        Assert.Empty(Directory.GetDirectories(_dir, ".staging-*"));
    }

    [Fact]
    public async Task A_failed_export_leaves_the_previous_one_in_place()
    {
        await Seed();
        await Exporter().ExportAsync(_dir, CancellationToken.None);
        var before = File.ReadAllText(Path.Combine(_dir, "locations.geojson"));

        await using var broken = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=x;Password=x;Timeout=1");
        await Assert.ThrowsAnyAsync<Exception>(() => Exporter(broken).ExportAsync(_dir, CancellationToken.None));

        Assert.Equal(before, File.ReadAllText(Path.Combine(_dir, "locations.geojson")));
        Assert.Empty(Directory.GetDirectories(_dir, ".staging-*"));
    }
}
