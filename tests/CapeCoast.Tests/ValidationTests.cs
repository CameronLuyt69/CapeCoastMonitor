using CapeCoast.Core.Domain;
using CapeCoast.Infrastructure.Ingestion;

namespace CapeCoast.Tests;

public class ValidationTests
{
    private static MarineObservation Marine(double? wave = 1.5, double? temp = 16) =>
        new(1, DateTime.UtcNow, wave, 1.2, 9, 220, 0.2, temp, 0.1);

    [Fact]
    public void Clean_row_has_no_flags()
    {
        Assert.Empty(ObservationValidator.Validate(Marine()).QualityFlags);
    }

    [Fact]
    public void Negative_wave_height_is_nulled_and_flagged()
    {
        var v = ObservationValidator.Validate(Marine(wave: -0.4));

        Assert.Null(v.WaveHeightM);
        Assert.Contains("out_of_range:wave_height_m", v.QualityFlags);
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(55)]
    public void Impossible_sea_temperature_is_flagged(double temp)
    {
        var v = ObservationValidator.Validate(Marine(temp: temp));

        Assert.Null(v.SeaTempC);
        Assert.Contains("out_of_range:sea_temp_c", v.QualityFlags);
    }

    [Fact]
    public void Missing_value_is_flagged_not_filled_in()
    {
        var v = ObservationValidator.Validate(Marine(wave: null));

        Assert.Null(v.WaveHeightM);
        Assert.Contains("missing:wave_height_m", v.QualityFlags);
    }

    [Fact]
    public void Wind_direction_above_360_is_flagged()
    {
        var v = ObservationValidator.Validate(new WindObservation(1, DateTime.UtcNow, 20, 30, 400));

        Assert.Null(v.DirectionDeg);
        Assert.Equal(20, v.SpeedKmh);
        Assert.Contains("out_of_range:direction_deg", v.QualityFlags);
    }

    [Fact]
    public void Source_without_a_success_is_stale()
    {
        var f = FreshnessChecker.Evaluate("x", null, 60, DateTime.UtcNow);
        Assert.True(f.Stale);
    }

    [Fact]
    public void Source_is_stale_after_three_missed_intervals()
    {
        var now = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(FreshnessChecker.Evaluate("x", now.AddMinutes(-170), 60, now).Stale);
        Assert.True(FreshnessChecker.Evaluate("x", now.AddMinutes(-190), 60, now).Stale);
    }
}

public class ResilienceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void A_pipeline_can_be_built_with_any_retry_count_including_none(int retries)
    {
        var pipeline = CapeCoast.Infrastructure.Resilience.ResiliencePipelines.ForHttpSource(
            new CapeCoast.Infrastructure.Sources.SourceOptions { Retries = retries, TimeoutSeconds = 5 });

        Assert.NotNull(pipeline);
    }

    [Fact]
    public async Task With_no_retries_a_server_error_comes_back_after_one_attempt()
    {
        var pipeline = CapeCoast.Infrastructure.Resilience.ResiliencePipelines.ForHttpSource(
            new CapeCoast.Infrastructure.Sources.SourceOptions { Retries = 0, TimeoutSeconds = 5 });
        var attempts = 0;

        var response = await pipeline.ExecuteAsync(_ =>
        {
            attempts++;
            return ValueTask.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        });

        Assert.Equal(1, attempts);
        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
    }
}

public class MigratorWaitTests
{
    [Fact]
    public void A_database_that_never_comes_up_fails_after_the_attempts_instead_of_hanging()
    {
        var started = DateTime.UtcNow;

        Assert.ThrowsAny<Exception>(() => CapeCoast.Infrastructure.Migrations.Migrator.WaitForServer(
            "Host=127.0.0.1;Port=1;Username=x;Password=x", Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            attempts: 3, delay: TimeSpan.FromMilliseconds(20)));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void It_retries_once_per_failed_attempt_and_then_gives_up()
    {
        var waits = 0;
        var logger = new CountingLogger(() => waits++);

        Assert.ThrowsAny<Exception>(() => CapeCoast.Infrastructure.Migrations.Migrator.WaitForServer(
            "Host=127.0.0.1;Port=1;Username=x;Password=x", logger, attempts: 3, delay: TimeSpan.FromMilliseconds(1)));

        Assert.Equal(2, waits);   // waited after attempts 1 and 2, and the third failure was thrown
    }
}

public sealed class CountingLogger : Microsoft.Extensions.Logging.ILogger
{
    private readonly Action _onLog;
    public CountingLogger(Action onLog) => _onLog = onLog;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter) => _onLog();
}
