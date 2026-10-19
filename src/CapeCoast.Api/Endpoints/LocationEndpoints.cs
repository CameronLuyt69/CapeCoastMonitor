using System.Globalization;
using CapeCoast.Core.Domain;
using CapeCoast.Infrastructure.Data;
using Microsoft.Extensions.Options;

namespace CapeCoast.Api.Endpoints;

public static class LocationEndpoints
{
    private const int MaxPoints = 5000;
    private const int MaxRangeDays = 400;
    private const int MaxRawRangeDays = 31;

    public static void MapLocationEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireRateLimiting(RateLimits.Public).WithTags("Public data");

        api.MapGet("/locations", GetLocations)
            .WithSummary("Latest conditions for every beach as a GeoJSON FeatureCollection")
            .WithDescription("Marine and wind values are the model value for the current hour (forecast data, not measurements). " +
                             "Water quality is the newest City sample, which is usually days old. Every block carries its own timestamps.");

        api.MapGet("/locations/{slug}/history", GetHistory)
            .WithSummary("Time series for one metric at one beach")
            .WithDescription("metric is one of the names listed in /api/metrics. interval is raw, day or week (default picks one from the range). " +
                             "Aggregated points carry min, max and avg; raw points carry the ingestion run that wrote them.");

        api.MapGet("/locations/{slug}/water-quality", GetWaterQuality)
            .WithSummary("Recent water quality samples with their source documents");

        api.MapGet("/metrics", () => MetricCatalog.Series.Select(m => new { key = m.Key, unit = m.Unit, hasDailyAggregate = m.DailyView is not null }))
            .WithSummary("Metrics that history can return");

        api.MapGet("/runs/{id:long}", GetRun)
            .WithSummary("One ingestion run, to trace a data point back to the job that wrote it");
    }

    private static async Task<IResult> GetLocations(ReadRepository repo, IOptions<WaterQualityDisplayOptions> wq, CancellationToken ct)
    {
        var threshold = wq.Value.SingleSampleThresholdCfu;
        var rows = await repo.LatestConditionsAsync(ct);

        var features = rows.Select(r => new
        {
            type = "Feature",
            id = r.Slug,
            geometry = new { type = "Point", coordinates = new[] { r.Lon, r.Lat } },
            properties = new
            {
                slug = r.Slug,
                name = r.Name,
                coast = r.Coast,
                citySiteCode = r.CitySiteCode,
                marine = r.MarineAt is null ? null : new
                {
                    observedAt = r.MarineAt,
                    forecastIssuedAt = r.MarineIssuedAt,
                    waveHeightM = r.WaveHeightM,
                    swellHeightM = r.SwellHeightM,
                    swellPeriodS = r.SwellPeriodS,
                    swellDirectionDeg = r.SwellDirectionDeg,
                    windWaveHeightM = r.WindWaveHeightM,
                    seaTempC = r.SeaTempC,
                    // an approximate proxy from the marine model, not official tide data
                    seaLevelMslApproxM = r.SeaLevelM,
                },
                wind = r.WindAt is null ? null : new
                {
                    observedAt = r.WindAt,
                    forecastIssuedAt = r.WindIssuedAt,
                    speedKmh = r.SpeedKmh,
                    gustKmh = r.GustKmh,
                    directionDeg = r.DirectionDeg,
                },
                waterQuality = r.WqSampledAt is null ? null : new
                {
                    sampleId = r.WqId,
                    sampledAt = r.WqSampledAt,
                    publishedAt = r.WqPublishedAt,
                    enterococciCfu100ml = r.WqCfu,
                    qualifier = r.WqQualifier,
                    rating = r.WqRating,
                    band = WaterQualityBand(r.WqCfu, r.WqQualifier, threshold),
                    documentUrl = r.WqDocumentUrl,
                },
            },
        });

        return Results.Json(new
        {
            type = "FeatureCollection",
            serverTime = DateTime.UtcNow,
            waterQualityThresholdCfu = threshold,
            features,
        });
    }

    /// <summary>ok, exceeds or unknown, against the configured single-sample threshold.</summary>
    public static string WaterQualityBand(int? cfu, string? qualifier, int threshold)
    {
        if (cfu is null) return "unknown";
        return qualifier switch
        {
            ">" => cfu >= threshold ? "exceeds" : "unknown",
            "<" => cfu <= threshold ? "ok" : "unknown",
            _ => cfu > threshold ? "exceeds" : "ok",
        };
    }

    private static async Task<IResult> GetHistory(string slug, string? metric, string? from, string? to, string? interval,
        ReadRepository repo, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();

        var def = metric is null ? null : MetricCatalog.Find(metric);
        if (metric is null) errors["metric"] = new[] { "metric is required" };
        else if (def is null) errors["metric"] = new[] { $"unknown metric, use one of: {string.Join(", ", MetricCatalog.Series.Select(m => m.Key))}" };

        var now = DateTime.UtcNow;
        var toUtc = ParseTime(to, now, "to", errors);
        var fromUtc = ParseTime(from, toUtc.AddDays(-7), "from", errors);

        if (errors.Count == 0)
        {
            if (fromUtc >= toUtc) errors["from"] = new[] { "from must be earlier than to" };
            else if ((toUtc - fromUtc).TotalDays > MaxRangeDays) errors["from"] = new[] { $"range is limited to {MaxRangeDays} days" };
        }

        var chosen = interval?.ToLowerInvariant();
        if (chosen is not null && chosen is not ("raw" or "day" or "week" or "auto"))
            errors["interval"] = new[] { "interval must be raw, day or week" };

        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var span = (toUtc - fromUtc).TotalDays;
        if (chosen is null or "auto") chosen = span <= 7 ? "raw" : span <= 90 ? "day" : "week";
        if (chosen == "raw" && span > MaxRawRangeDays)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["interval"] = new[] { $"raw data is limited to {MaxRawRangeDays} days, use interval=day or week for longer ranges" }
            });

        var location = await repo.FindLocationAsync(slug, ct);
        if (location is null) return Results.NotFound(new { error = "unknown beach" });

        IReadOnlyList<HistoryPoint> points = chosen == "raw"
            ? await repo.RawHistoryAsync(def!, location.Id, fromUtc, toUtc, MaxPoints + 1, ct)
            : await repo.AggregatedHistoryAsync(def!, location.Id, fromUtc, toUtc, chosen, MaxPoints + 1, ct);

        var truncated = points.Count > MaxPoints;
        if (truncated) points = points.Take(MaxPoints).ToList();

        object output = chosen == "raw"
            ? points.Select(p => new { t = p.T, value = p.Value, ingestionRunId = p.IngestionRunId, flags = p.QualityFlags })
            : points.Select(p => new { t = p.T, min = p.Min, max = p.Max, avg = p.Avg });

        return Results.Json(new
        {
            location = new { slug = location.Slug, name = location.Name },
            metric = def!.Key,
            unit = def.Unit,
            interval = chosen,
            from = fromUtc,
            to = toUtc,
            truncated,
            points = output,
        });
    }

    private static DateTime ParseTime(string? text, DateTime fallback, string name, Dictionary<string, string[]> errors)
    {
        if (text is null) return fallback;
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return parsed.UtcDateTime;
        errors[name] = new[] { "use an ISO 8601 date or time, for example 2026-10-19T00:00:00Z" };
        return fallback;
    }

    private static async Task<IResult> GetWaterQuality(string slug, int? limit, ReadRepository repo,
        IOptions<WaterQualityDisplayOptions> wq, CancellationToken ct)
    {
        if (limit is < 1 or > 100)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["limit"] = new[] { "limit must be between 1 and 100" } });

        var location = await repo.FindLocationAsync(slug, ct);
        if (location is null) return Results.NotFound(new { error = "unknown beach" });

        var threshold = wq.Value.SingleSampleThresholdCfu;
        var rows = await repo.WaterQualityAsync(location.Id, limit ?? 10, ct);

        return Results.Json(new
        {
            location = new { slug = location.Slug, name = location.Name },
            thresholdCfu = threshold,
            samples = rows.Select(s => new
            {
                id = s.Id,
                sampledAt = s.SampledAt,
                publishedAt = s.PublishedAt,
                enterococciCfu100ml = s.Cfu,
                qualifier = s.Qualifier,
                rating = s.Rating,
                band = WaterQualityBand(s.Cfu, s.Qualifier, threshold),
                ingestionRunId = s.IngestionRunId,
                // the City's own URL, the stored copy is not served
                document = s.DocumentId is null ? null : new
                {
                    id = s.DocumentId,
                    title = s.DocumentTitle,
                    url = s.DocumentUrl,
                    fetchedAt = s.DocumentFetchedAt,
                },
            }),
        });
    }

    private static async Task<IResult> GetRun(long id, ReadRepository repo, CancellationToken ct)
    {
        var run = await repo.RunAsync(id, ct);
        if (run is null) return Results.NotFound(new { error = "unknown run" });
        return Results.Json(new
        {
            id = run.Id,
            source = run.Source,
            startedAt = run.StartedAt,
            finishedAt = run.FinishedAt,
            status = run.Status,
            rowsFetched = run.RowsFetched,
            rowsWritten = run.RowsWritten,
            message = run.ErrorMessage is { Length: > 300 } m ? m[..300] : run.ErrorMessage,
        });
    }
}
