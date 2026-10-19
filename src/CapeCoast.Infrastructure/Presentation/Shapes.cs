using CapeCoast.Core.Domain;
using CapeCoast.Infrastructure.Data;

namespace CapeCoast.Infrastructure.Presentation;

/// <summary>
/// The JSON shapes the API returns, built in one place so the static export produces exactly what the API does.
/// </summary>
public static class Shapes
{
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

    /// <param name="includeWaterQuality">False leaves the water quality block out and says so, for exports that must not carry the City's results.</param>
    public static object Locations(IEnumerable<LatestConditionsRow> rows, int threshold, bool includeWaterQuality, DateTime serverTime)
    {
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
                surf = Surf(r),
                weather = r.WeatherAt is null ? null : new
                {
                    observedAt = r.WeatherAt,
                    forecastIssuedAt = r.WeatherIssuedAt,
                    airTempC = r.AirTempC,
                    precipitationMm = r.PrecipitationMm,
                    uvIndex = r.UvIndex,
                    visibilityM = r.VisibilityM,
                    cloudCoverPct = r.CloudCoverPct,
                    humidityPct = r.HumidityPct,
                },
                waterQuality = !includeWaterQuality || r.WqSampledAt is null ? null : new
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

        return new
        {
            type = "FeatureCollection",
            serverTime,
            waterQualityIncluded = includeWaterQuality,
            waterQualityThresholdCfu = threshold,
            features,
        };
    }

    /// <summary>
    /// The derived surf estimate for a beach right now. Every input is the model value for the current hour, so
    /// it is an estimate from the forecast and says so. A missing input leaves the score empty and lists what is missing.
    /// </summary>
    private static object Surf(LatestConditionsRow r)
    {
        var result = SurfScore.Calculate(new SurfInputs(r.SwellHeightM, r.SwellPeriodS, r.SwellDirectionDeg,
            r.SpeedKmh, r.DirectionDeg, r.FacingDeg));

        return new
        {
            score = result.Score,
            label = result.Label,
            sizePoints = result.SizePoints is null ? (double?)null : Math.Round(result.SizePoints.Value, 1),
            periodPoints = result.PeriodPoints is null ? (double?)null : Math.Round(result.PeriodPoints.Value, 1),
            windPoints = result.WindPoints is null ? (double?)null : Math.Round(result.WindPoints.Value, 1),
            maxPoints = new { size = SurfScore.MaxSize, period = SurfScore.MaxPeriod, wind = SurfScore.MaxWind },
            missing = result.Missing,
            facingDeg = r.FacingDeg,
            facingSource = r.FacingSource,
            note = "Estimate from the forecast, not a surf report. See docs/surf-score.md.",
        };
    }

    public static object WaterQuality(LocationRef location, IEnumerable<WaterQualitySampleRow> rows, int threshold) => new
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
    };

    public static object History(LocationRef location, MetricDef metric, string interval, DateTime from, DateTime to,
        bool truncated, IReadOnlyList<HistoryPoint> points)
    {
        object output = interval == "raw"
            ? points.Select(p => new { t = p.T, value = p.Value, ingestionRunId = p.IngestionRunId, flags = p.QualityFlags })
            : points.Select(p => new { t = p.T, min = p.Min, max = p.Max, avg = p.Avg });

        return new
        {
            location = new { slug = location.Slug, name = location.Name },
            metric = metric.Key,
            unit = metric.Unit,
            interval,
            from,
            to,
            truncated,
            points = output,
        };
    }

    public static object Run(IngestionRunRow run) => new
    {
        id = run.Id,
        source = run.Source,
        startedAt = run.StartedAt,
        finishedAt = run.FinishedAt,
        status = run.Status,
        rowsFetched = run.RowsFetched,
        rowsWritten = run.RowsWritten,
        message = run.ErrorMessage is { Length: > 300 } m ? m[..300] : run.ErrorMessage,
    };
}
