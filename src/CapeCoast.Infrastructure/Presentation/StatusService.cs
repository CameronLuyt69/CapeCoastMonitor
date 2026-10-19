using CapeCoast.Infrastructure.Data;
using CapeCoast.Infrastructure.Ingestion;
using CapeCoast.Infrastructure.Sources;
using CapeCoast.Infrastructure.Sources.WaterQuality;
using Microsoft.Extensions.Options;

namespace CapeCoast.Infrastructure.Presentation;

/// <summary>Per-source freshness and recent runs, shared by /api/status and the static export.</summary>
public class StatusService
{
    private readonly ReadRepository _repo;
    private readonly IngestionRunRepository _runs;
    private readonly SourcesOptions _options;

    public StatusService(ReadRepository repo, IngestionRunRepository runs, IOptions<SourcesOptions> options)
    {
        _repo = repo;
        _runs = runs;
        _options = options.Value;
    }

    public async Task<object> BuildAsync(CancellationToken ct)
    {
        var sources = new (string Name, SourceOptions Options)[]
        {
            (OpenMeteoMarineSource.SourceName, _options.OpenMeteoMarine),
            (OpenMeteoWindSource.SourceName, _options.OpenMeteoWind),
            (OpenMeteoWeatherSource.SourceName, _options.OpenMeteoWeather),
            (CityReportSource.SourceName, _options.CityWaterQuality),
        };

        var latestData = await _repo.LatestDataTimesAsync(ct);
        var now = DateTime.UtcNow;
        var result = new List<object>();

        foreach (var (name, so) in sources)
        {
            var status = await _runs.GetStatusAsync(name, ct);
            var freshness = FreshnessChecker.Evaluate(name, status.LastSuccessAt, so.IntervalMinutes, now);
            var recent = await _repo.RecentRunsAsync(name, 5, ct);

            result.Add(new
            {
                source = name,
                enabled = so.Enabled,
                stale = so.Enabled && freshness.Stale,
                staleReason = so.Enabled && freshness.Stale ? freshness.Reason : null,
                expectedEveryMinutes = so.IntervalMinutes,
                lastSuccessAt = status.LastSuccessAt,
                lastRunAt = status.LastRunAt,
                lastStatus = status.LastStatus,
                latestDataAt = latestData.GetValueOrDefault(name),
                recentRuns = recent.Select(r => Shapes.Run(r)),
            });
        }

        return new { serverTime = now, sources = result };
    }
}
