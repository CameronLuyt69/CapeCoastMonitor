using System.Diagnostics;
using System.Net;
using CapeCoast.Api;
using CapeCoast.Infrastructure.Data;
using CapeCoast.Infrastructure.Ingestion;
using CapeCoast.Infrastructure.Telemetry;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using AppTelemetry = CapeCoast.Infrastructure.Telemetry.Telemetry;

namespace CapeCoast.Tests;

public class PercentileTests
{
    private static readonly double[] Bounds = { 1, 2, 5 };

    [Fact]
    public void No_samples_means_no_estimate()
    {
        Assert.Null(Percentiles.Estimate(Bounds, new long[] { 0, 0, 0, 0 }, 0.5, null, null));
    }

    [Fact]
    public void A_percentile_is_interpolated_inside_its_bucket()
    {
        // ten samples all between 1 and 2, so the median sits halfway through that bucket
        Assert.Equal(1.5, Percentiles.Estimate(Bounds, new long[] { 0, 10, 0, 0 }, 0.5, null, null)!.Value, 6);
    }

    [Fact]
    public void Percentiles_spread_across_buckets()
    {
        var counts = new long[] { 5, 5, 0, 0 };

        Assert.Equal(1.0, Percentiles.Estimate(Bounds, counts, 0.5, null, null)!.Value, 6);
        Assert.Equal(1.9, Percentiles.Estimate(Bounds, counts, 0.95, null, null)!.Value, 6);
    }

    [Fact]
    public void The_recorded_min_and_max_tighten_the_estimate()
    {
        var value = Percentiles.Estimate(Bounds, new long[] { 10, 0, 0, 0 }, 0.5, min: 0.4, max: 0.6);

        Assert.Equal(0.5, value!.Value, 6);
    }

    [Fact]
    public void The_overflow_bucket_is_bounded_by_the_recorded_max()
    {
        var value = Percentiles.Estimate(Bounds, new long[] { 0, 0, 0, 4 }, 0.5, null, max: 9);

        Assert.Equal(7.0, value!.Value, 6);   // halfway between the last bound (5) and the max (9)
    }

    [Fact]
    public void Higher_percentiles_are_never_lower()
    {
        var counts = new long[] { 40, 30, 20, 10 };
        var p50 = Percentiles.Estimate(Bounds, counts, 0.5, 0.1, 12)!.Value;
        var p95 = Percentiles.Estimate(Bounds, counts, 0.95, 0.1, 12)!.Value;
        var p99 = Percentiles.Estimate(Bounds, counts, 0.99, 0.1, 12)!.Value;

        Assert.True(p50 <= p95 && p95 <= p99);
        Assert.True(p99 <= 12);
    }

    [Fact]
    public void Mismatched_arrays_are_a_programming_error()
    {
        Assert.Throws<ArgumentException>(() => Percentiles.Estimate(Bounds, new long[] { 1, 2 }, 0.5, null, null));
    }
}

public class MetricRowBuilderTests
{
    private static readonly DateTime At = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
    private static readonly double[] Bounds = { 0.01, 0.1, 1 };

    private static HistogramSnapshot Request(string route, string status, long[] counts, double max = 0.5) => new(
        MetricRowBuilder.HttpServerDuration,
        new Dictionary<string, string> { ["http.route"] = route, ["http.response.status_code"] = status, ["http.request.method"] = "GET" },
        counts.Sum(), counts.Sum() * 0.05, 0.001, max, Bounds, counts);

    private static double Value(IEnumerable<MetricRow> rows, string name, string attribute) =>
        rows.Single(r => r.Name == name && r.AttributesJson.Contains(attribute)).Value;

    [Fact]
    public void Requests_are_summarised_per_route_with_methods_and_statuses_merged()
    {
        var rows = MetricRowBuilder.Histograms(At, new[]
        {
            Request("/api/locations", "200", new long[] { 3, 1, 0, 0 }),
            Request("/api/locations", "404", new long[] { 0, 1, 0, 0 }),
            Request("/api/health", "200", new long[] { 10, 0, 0, 0 }),
        }, Array.Empty<string>());

        Assert.Equal(5, Value(rows, "http.server.request.duration.count", "/api/locations"));
        Assert.Equal(10, Value(rows, "http.server.request.duration.count", "/api/health"));
        Assert.Equal(15, Value(rows, "http.server.request.duration.count", "\"scope\":\"all\""));
    }

    [Fact]
    public void Each_series_carries_count_sum_max_and_three_percentiles()
    {
        var rows = MetricRowBuilder.Histograms(At, new[] { Request("/api/locations", "200", new long[] { 3, 1, 0, 0 }) }, Array.Empty<string>());

        var names = rows.Where(r => r.AttributesJson.Contains("/api/locations")).Select(r => r.Name).ToHashSet();
        foreach (var suffix in new[] { "count", "sum", "max", "p50", "p95", "p99" })
            Assert.Contains($"http.server.request.duration.{suffix}", names);
    }

    [Fact]
    public void Long_lived_routes_are_kept_out_of_the_all_routes_percentiles()
    {
        var rows = MetricRowBuilder.Histograms(At, new[]
        {
            Request("/api/locations", "200", new long[] { 10, 0, 0, 0 }, max: 0.008),
            Request("/api/stream", "200", new long[] { 0, 0, 0, 5 }, max: 3600),   // a stream open for an hour
        }, new[] { "/api/stream" });

        Assert.Equal(10, Value(rows, "http.server.request.duration.count", "\"scope\":\"all\""));
        Assert.True(Value(rows, "http.server.request.duration.p99", "\"scope\":\"all\"") < 0.01);
        // it is still reported on its own route
        Assert.Equal(5, Value(rows, "http.server.request.duration.count", "/api/stream"));
    }

    [Fact]
    public void Other_histograms_keep_their_own_tag_sets()
    {
        var snapshots = new[]
        {
            new HistogramSnapshot("ccm.job.duration", new Dictionary<string, string> { ["job"] = "marine-ingest", ["outcome"] = "ok" }, 2, 3, 1, 2, Bounds, new long[] { 0, 0, 2, 0 }),
            new HistogramSnapshot("ccm.job.duration", new Dictionary<string, string> { ["job"] = "wind-ingest", ["outcome"] = "ok" }, 1, 1, 1, 1, Bounds, new long[] { 0, 0, 1, 0 }),
        };

        var rows = MetricRowBuilder.Histograms(At, snapshots, Array.Empty<string>());

        Assert.Equal(2, Value(rows, "ccm.job.duration.count", "marine-ingest"));
        Assert.Equal(1, Value(rows, "ccm.job.duration.count", "wind-ingest"));
        Assert.DoesNotContain(rows, r => r.AttributesJson.Contains("scope"));
    }

    [Fact]
    public void Histograms_with_different_bucket_bounds_are_not_added_together()
    {
        var a = Request("/api/locations", "200", new long[] { 4, 0, 0, 0 });
        var b = a with { UpperBounds = new[] { 0.5, 5 }, Counts = new long[] { 100, 0, 0 }, Count = 100 };

        var rows = MetricRowBuilder.Histograms(At, new[] { a, b }, Array.Empty<string>());

        Assert.Equal(4, Value(rows, "http.server.request.duration.count", "/api/locations"));
    }
}

[Collection("db")]
public class MetricExporterTests
{
    private readonly DatabaseFixture _fx;

    public MetricExporterTests(DatabaseFixture fx) => _fx = fx;

    private async Task<List<(string Name, string Attributes, double Value, string Service)>> Rows(string like)
    {
        await using var conn = await _fx.Db.OpenConnectionAsync();
        var rows = await conn.QueryAsync<(string, string, double, string)>(
            "SELECT name, attributes::text, value, service FROM metric_points WHERE name LIKE @like ORDER BY name", new { like });
        return rows.ToList();
    }

    [Fact]
    public async Task Recorded_metrics_end_up_in_the_table_with_percentiles()
    {
        await using (var conn = await _fx.Db.OpenConnectionAsync())
            await conn.ExecuteAsync("DELETE FROM metric_points");

        using var meter = new System.Diagnostics.Metrics.Meter("ccm-test-meter");
        var histogram = meter.CreateHistogram<double>("ccm.test.duration", "s");
        var counter = meter.CreateCounter<long>("ccm.test.rows");
        var ignored = meter.CreateCounter<long>("other.unwanted");

        var exporter = new PostgresMetricExporter(_fx.Db, "test-service");
        using var provider = Sdk.CreateMeterProviderBuilder()
            .AddMeter("ccm-test-meter")
            .AddReader(new PeriodicExportingMetricReader(exporter, 3_600_000) { TemporalityPreference = MetricReaderTemporalityPreference.Delta })
            .Build();

        foreach (var seconds in new[] { 0.02, 0.03, 0.04, 0.5 }) histogram.Record(seconds, new KeyValuePair<string, object?>("job", "x"));
        counter.Add(7, new KeyValuePair<string, object?>("source", "marine"));
        ignored.Add(1);
        Assert.True(provider.ForceFlush(10_000));

        var duration = await Rows("ccm.test.duration.%");
        Assert.Equal(new[] { "count", "max", "p50", "p95", "p99", "sum" }, duration.Select(r => r.Name.Split('.').Last()).OrderBy(n => n));
        Assert.Equal(4, duration.Single(r => r.Name.EndsWith(".count")).Value);
        Assert.Contains("\"job\": \"x\"", duration[0].Attributes);
        Assert.Equal("test-service", duration[0].Service);
        Assert.True(duration.Single(r => r.Name.EndsWith(".p50")).Value < duration.Single(r => r.Name.EndsWith(".p99")).Value);
        Assert.Equal(0.5, duration.Single(r => r.Name.EndsWith(".max")).Value, 6);

        var rows = await Rows("ccm.test.rows");
        Assert.Equal(7, Assert.Single(rows).Value);
        Assert.Contains("\"source\": \"marine\"", rows[0].Attributes);

        Assert.Empty(await Rows("other.%"));
    }

    [Fact]
    public async Task A_database_that_is_down_fails_the_export_without_throwing()
    {
        using var meter = new System.Diagnostics.Metrics.Meter("ccm-test-meter-down");
        var counter = meter.CreateCounter<long>("ccm.test.down");
        await using var broken = Npgsql.NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=x;Password=x;Timeout=1");

        using var provider = Sdk.CreateMeterProviderBuilder()
            .AddMeter("ccm-test-meter-down")
            .AddReader(new PeriodicExportingMetricReader(new PostgresMetricExporter(broken, "x"), 3_600_000)
            { TemporalityPreference = MetricReaderTemporalityPreference.Delta })
            .Build();
        counter.Add(1);

        var flushed = provider.ForceFlush(10_000);

        Assert.False(flushed);   // reported as a failed export, and the app carried on
    }

    [Fact]
    public async Task The_ingestion_run_metrics_use_the_documented_names()
    {
        await using (var conn = await _fx.Db.OpenConnectionAsync())
            await conn.ExecuteAsync("DELETE FROM metric_points");

        var exporter = new PostgresMetricExporter(_fx.Db, "worker");
        using var provider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(AppTelemetry.Name)
            .AddReader(new PeriodicExportingMetricReader(exporter, 3_600_000) { TemporalityPreference = MetricReaderTemporalityPreference.Delta })
            .Build();

        AppTelemetry.RecordRun("open-meteo-marine", "failed", 0, 0, TimeSpan.FromSeconds(1.5));
        AppTelemetry.RecordRun("open-meteo-marine", "succeeded", 960, 960, TimeSpan.FromSeconds(0.4));
        provider.ForceFlush(10_000);

        var runs = await Rows("ccm.ingestion.runs");
        Assert.Equal(2, runs.Count);
        Assert.Contains(runs, r => r.Attributes.Contains("\"outcome\": \"failed\"") && r.Value == 1);   // failures per source
        Assert.Equal(960, (await Rows("ccm.ingestion.rows.written")).Single().Value);
        Assert.Equal(2, (await Rows("ccm.ingestion.run.duration.count")).Sum(r => r.Value));
    }
}

public class CorrelationTests
{
    [Fact]
    public void A_well_formed_incoming_id_is_kept()
    {
        Assert.Equal("req-123.abc_DEF", CorrelationMiddleware.Resolve("req-123.abc_DEF", "trace"));
    }

    [Theory]
    [InlineData("has spaces")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("a\"b")]
    [InlineData("")]
    [InlineData("this-id-is-far-too-long-to-be-a-sensible-correlation-id-and-would-only-bloat-the-logs")]
    public void Anything_else_is_replaced_by_the_trace_id(string incoming)
    {
        Assert.Equal("trace", CorrelationMiddleware.Resolve(incoming, "trace"));
    }

    [Fact]
    public void With_neither_a_fresh_id_is_made_up()
    {
        var id = CorrelationMiddleware.Resolve(null, null);

        Assert.Equal(32, id.Length);
        Assert.NotEqual(id, CorrelationMiddleware.Resolve(null, null));
    }

    [Fact]
    public void Starting_an_activity_always_gives_a_trace_id_even_with_nothing_listening()
    {
        using var activity = AppTelemetry.StartActivity("test job");

        Assert.NotEqual(default, activity.TraceId);
        Assert.Equal(activity.TraceId, Activity.Current!.TraceId);
    }
}

[Collection("db")]
public class CorrelationIntegrationTests
{
    private readonly DatabaseFixture _fx;

    public CorrelationIntegrationTests(DatabaseFixture fx) => _fx = fx;

    [Fact]
    public async Task Every_response_carries_a_correlation_id()
    {
        await using var host = await ApiTestHost.StartAsync(_fx.ConnectionString);

        var response = await host.Client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var id = Assert.Single(response.Headers.GetValues("X-Correlation-Id"));
        Assert.Matches("^[A-Za-z0-9._-]{1,64}$", id);
    }

    [Fact]
    public async Task A_caller_supplied_id_is_echoed_back_so_a_request_can_be_followed_end_to_end()
    {
        await using var host = await ApiTestHost.StartAsync(_fx.ConnectionString);
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add("X-Correlation-Id", "my-trace-42");

        var response = await host.Client.SendAsync(request);

        Assert.Equal("my-trace-42", response.Headers.GetValues("X-Correlation-Id").Single());
    }

    [Fact]
    public async Task A_malformed_caller_id_is_not_trusted()
    {
        await using var host = await ApiTestHost.StartAsync(_fx.ConnectionString);
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", "<script>x</script>");

        var response = await host.Client.SendAsync(request);

        Assert.NotEqual("<script>x</script>", response.Headers.GetValues("X-Correlation-Id").Single());
    }

    [Fact]
    public async Task An_ingestion_run_records_the_trace_id_of_the_job_that_started_it()
    {
        var runs = new IngestionRunRepository(_fx.Db);
        var service = new IngestionService(new LocationRepository(_fx.Db), new ObservationRepository(_fx.Db), runs,
            NullLogger<IngestionService>.Instance);

        using var job = AppTelemetry.StartActivity("job under test");
        var result = await service.RunWindAsync(new BrokenWindSourceForTrace(), CancellationToken.None);

        await using var conn = await _fx.Db.OpenConnectionAsync();
        var stored = await conn.ExecuteScalarAsync<string>("SELECT correlation_id FROM ingestion_runs WHERE id = @id", new { id = result.RunId });
        Assert.Equal(job.TraceId.ToString(), stored);
    }

    private class BrokenWindSourceForTrace : CapeCoast.Core.Sources.IWindSource
    {
        public string Name => "trace-test";
        public Task<IReadOnlyList<CapeCoast.Core.Domain.WindObservation>> FetchAsync(
            IReadOnlyList<CapeCoast.Core.Domain.Location> l, CancellationToken ct) => throw new HttpRequestException("down");
    }
}
