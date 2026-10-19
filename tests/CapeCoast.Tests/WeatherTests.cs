using CapeCoast.Core.Domain;
using CapeCoast.Core.Sources;
using CapeCoast.Infrastructure.Data;
using CapeCoast.Infrastructure.Ingestion;
using CapeCoast.Infrastructure.Sources;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;

namespace CapeCoast.Tests;

public class WeatherParserTests
{
    private static readonly Location Muizenberg = new(6, "Muizenberg", "muizenberg", -34.109, 18.471);

    private static string Fixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "weather-extra-muizenberg.json"));

    [Fact]
    public void The_real_probe_response_parses_every_hour_and_field()
    {
        var rows = OpenMeteoWeatherSource.Parse(Fixture(), new[] { Muizenberg });

        Assert.Equal(48, rows.Count);
        Assert.All(rows, r => Assert.Equal(DateTimeKind.Utc, r.ObservedAt.Kind));
        Assert.Equal(10.7, rows[0].AirTempC);
        Assert.Equal(0.0, rows[0].PrecipitationMm);
        Assert.Equal(41640, rows[0].VisibilityM);
        Assert.Equal(22, rows[0].CloudCoverPct);
        Assert.Equal(76, rows[0].HumidityPct);
        Assert.Contains(rows, r => r.UvIndex > 5);   // the probe day reaches 7.15 around midday
    }

    [Fact]
    public void A_null_in_the_response_stays_null()
    {
        const string json = @"{""hourly"":{""time"":[""2026-10-19T00:00""],""temperature_2m"":[null],""uv_index"":[3.2]}}";

        var row = Assert.Single(OpenMeteoWeatherSource.Parse(json, new[] { Muizenberg }));

        Assert.Null(row.AirTempC);
        Assert.Equal(3.2, row.UvIndex);
        Assert.Null(row.VisibilityM);
    }

    [Fact]
    public void Values_inside_their_ranges_are_left_alone()
    {
        var o = new WeatherObservation(1, DateTime.UtcNow, 18, 0.4, 7, 30000, 50, 80);

        var v = ObservationValidator.Validate(o);

        Assert.Empty(v.QualityFlags);
        Assert.Equal(o.AirTempC, v.AirTempC);
    }

    [Theory]
    [InlineData(80, "air_temp_c")]
    [InlineData(-40, "air_temp_c")]
    public void An_impossible_air_temperature_is_nulled_and_flagged(double temp, string field)
    {
        var v = ObservationValidator.Validate(new WeatherObservation(1, DateTime.UtcNow, temp, 0, 1, 1000, 10, 10));

        Assert.Null(v.AirTempC);
        Assert.Contains($"out_of_range:{field}", v.QualityFlags);
    }

    [Fact]
    public void Negative_rain_a_uv_index_off_the_scale_and_humidity_over_100_are_all_rejected()
    {
        var v = ObservationValidator.Validate(new WeatherObservation(1, DateTime.UtcNow, 15, -1, 31, -5, 120, 130));

        Assert.Null(v.PrecipitationMm);
        Assert.Null(v.UvIndex);
        Assert.Null(v.VisibilityM);
        Assert.Null(v.CloudCoverPct);
        Assert.Null(v.HumidityPct);
        Assert.Equal(15, v.AirTempC);
        Assert.Equal(5, v.QualityFlags.Count(f => f.StartsWith("out_of_range")));
    }

    [Fact]
    public void A_missing_value_is_flagged_not_filled_in()
    {
        var v = ObservationValidator.Validate(new WeatherObservation(1, DateTime.UtcNow, null, 0, 1, 1000, 10, 10));

        Assert.Null(v.AirTempC);
        Assert.Contains("missing:air_temp_c", v.QualityFlags);
    }

    [Fact]
    public void The_new_metrics_are_in_the_catalog_with_a_daily_aggregate()
    {
        foreach (var key in new[] { "air_temp", "precipitation", "uv_index", "visibility", "cloud_cover", "humidity" })
        {
            var metric = MetricCatalog.Find(key);
            Assert.NotNull(metric);
            Assert.Equal("weather_observations", metric!.Table);
            Assert.Equal("weather_daily", metric.DailyView);
            Assert.True(MetricCatalog.IsAlertMetric(key));
        }
    }
}

[Collection("db")]
public class WeatherIngestionTests
{
    private readonly DatabaseFixture _fx;

    public WeatherIngestionTests(DatabaseFixture fx) => _fx = fx;

    private class FixtureWeatherSource : IWeatherSource
    {
        public string Name => "fixture-weather";

        public Task<IReadOnlyList<WeatherObservation>> FetchAsync(IReadOnlyList<Location> locations, CancellationToken ct)
        {
            var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "weather-extra-muizenberg.json"));
            return Task.FromResult(OpenMeteoWeatherSource.Parse(json, new[] { locations[0] }));
        }
    }

    private class OutOfRangeWeatherSource : IWeatherSource
    {
        public string Name => "fixture-weather";

        public Task<IReadOnlyList<WeatherObservation>> FetchAsync(IReadOnlyList<Location> locations, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WeatherObservation>>(new[]
            {
                new WeatherObservation(locations[0].Id, new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc), 400, 0, 1, 1000, 10, 10),
            });
    }

    private IngestionService Service() => new(new LocationRepository(_fx.Db), new ObservationRepository(_fx.Db),
        new IngestionRunRepository(_fx.Db), NullLogger<IngestionService>.Instance);

    private async Task Reset()
    {
        await using var conn = await _fx.Db.OpenConnectionAsync();
        await conn.ExecuteAsync("DELETE FROM weather_observations WHERE source = 'fixture-weather'");
    }

    private async Task<long> Count()
    {
        await using var conn = await _fx.Db.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM weather_observations WHERE source = 'fixture-weather'");
    }

    [Fact]
    public async Task Running_the_weather_job_twice_creates_no_duplicates_and_changes_nothing_the_second_time()
    {
        await Reset();
        var service = Service();

        var first = await service.RunWeatherAsync(new FixtureWeatherSource(), CancellationToken.None);
        var second = await service.RunWeatherAsync(new FixtureWeatherSource(), CancellationToken.None);

        Assert.Equal("succeeded", first.Status);
        Assert.Equal(48, first.Written);
        Assert.Equal(0, second.Written);
        Assert.Equal(48, await Count());
    }

    [Fact]
    public async Task An_out_of_range_value_is_stored_empty_with_a_flag_and_the_run_is_flagged()
    {
        await Reset();

        var result = await Service().RunWeatherAsync(new OutOfRangeWeatherSource(), CancellationToken.None);

        Assert.Equal("flagged", result.Status);
        await using var conn = await _fx.Db.OpenConnectionAsync();
        var row = await conn.QuerySingleAsync<(double? Temp, string[] Flags)>(
            "SELECT air_temp_c, quality_flags FROM weather_observations WHERE source = 'fixture-weather'");
        Assert.Null(row.Temp);
        Assert.Contains("out_of_range:air_temp_c", row.Flags);
    }
}
