using CapeCoast.Infrastructure.Data;
using CapeCoast.Infrastructure.Ingestion;
using CapeCoast.Infrastructure.Sources;
using CapeCoast.Infrastructure.Sources.WaterQuality;
using Microsoft.Extensions.Options;

namespace CapeCoast.Api.Endpoints;

public static class StatusEndpoints
{
    public static void MapStatusEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireRateLimiting(RateLimits.Public).WithTags("Status");

        api.MapGet("/health", Health)
            .WithSummary("Liveness and database connectivity");

        api.MapGet("/status", Status)
            .WithSummary("Per-source freshness and the last run results");
    }

    private static async Task<IResult> Health(ReadRepository repo, CancellationToken ct)
    {
        try
        {
            var ok = await repo.PingAsync(ct);
            return ok ? Results.Ok(new { status = "ok", database = "up" })
                      : Results.Json(new { status = "degraded", database = "down" }, statusCode: 503);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return Results.Json(new { status = "degraded", database = "down" }, statusCode: 503);
        }
    }

    private static async Task<IResult> Status(ReadRepository repo, IngestionRunRepository runs,
        IOptions<SourcesOptions> options, CancellationToken ct)
    {
        var o = options.Value;
        var sources = new (string Name, SourceOptions Options)[]
        {
            (OpenMeteoMarineSource.SourceName, o.OpenMeteoMarine),
            (OpenMeteoWindSource.SourceName, o.OpenMeteoWind),
            (CityReportSource.SourceName, o.CityWaterQuality),
        };

        var latestData = await repo.LatestDataTimesAsync(ct);
        var now = DateTime.UtcNow;
        var result = new List<object>();

        foreach (var (name, so) in sources)
        {
            var status = await runs.GetStatusAsync(name, ct);
            var freshness = FreshnessChecker.Evaluate(name, status.LastSuccessAt, so.IntervalMinutes, now);
            var recent = await repo.RecentRunsAsync(name, 5, ct);

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
                recentRuns = recent.Select(r => new
                {
                    id = r.Id,
                    startedAt = r.StartedAt,
                    finishedAt = r.FinishedAt,
                    status = r.Status,
                    rowsFetched = r.RowsFetched,
                    rowsWritten = r.RowsWritten,
                    message = r.ErrorMessage is { Length: > 300 } m ? m[..300] : r.ErrorMessage,
                }),
            });
        }

        return Results.Json(new { serverTime = now, sources = result });
    }
}
