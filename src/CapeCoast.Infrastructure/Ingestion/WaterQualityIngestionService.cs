using System.Diagnostics;
using CapeCoast.Core.Alerts;
using CapeCoast.Core.Domain;
using CapeCoast.Core.Sources;
using CapeCoast.Infrastructure.Data;
using CapeCoast.Infrastructure.Sources;
using CapeCoast.Infrastructure.Sources.WaterQuality;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CapeCoast.Infrastructure.Ingestion;

/// <summary>
/// Daily check for a new City report. The PDF is kept and hashed first, so even a report that
/// fails the confidence check stays traceable. Samples are only written from a parse that
/// looks trustworthy; otherwise the run is flagged and the database is left alone.
/// </summary>
public class WaterQualityIngestionService
{
    private readonly WaterQualityRepository _samples;
    private readonly IngestionRunRepository _runs;
    private readonly WaterQualityOptions _options;
    private readonly ILogger<WaterQualityIngestionService> _log;
    private readonly IPostIngestHook? _hook;

    public WaterQualityIngestionService(WaterQualityRepository samples, IngestionRunRepository runs,
        IOptions<SourcesOptions> options, ILogger<WaterQualityIngestionService> log, IPostIngestHook? hook = null)
    {
        _hook = hook;
        _samples = samples;
        _runs = runs;
        _options = options.Value.CityWaterQuality;
        _log = log;
    }

    public async Task<IngestionResult> RunAsync(IWaterQualitySource source, CancellationToken ct)
    {
        var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        var runId = await _runs.StartAsync(source.Name, correlationId, ct);
        using var span = Telemetry.Telemetry.Source.StartActivity("ingestion.run");
        span?.SetTag("ccm.source", source.Name);
        span?.SetTag("ccm.run_id", runId);
        var clock = Stopwatch.StartNew();
        IngestionResult Done(IngestionResult r)
        {
            Telemetry.Telemetry.RecordRun(source.Name, r.Status, r.Fetched, r.Written, clock.Elapsed);
            if (r.Status == "failed") span?.SetStatus(ActivityStatusCode.Error, r.Error);
            return r;
        }
        using var scope = _log.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["Source"] = source.Name,
            ["RunId"] = runId
        });

        try
        {
            var download = await source.DownloadAsync(ct);

            if (await _samples.FindDocumentAsync(download.Sha256, ct) is not null)
            {
                await _runs.FinishAsync(runId, "succeeded", 0, 0, null, ct);
                _log.LogInformation("Report unchanged since the last download, nothing to do");
                return Done(new IngestionResult(runId, "succeeded", 0, 0, null));
            }

            var storedPath = Store(download);
            var documentId = await _samples.InsertDocumentAsync(source.Name, download.Url, download.Sha256,
                storedPath, "Coastal Water Quality Report", download.PublishedAt, ct);

            var report = source.Parse(download.Content);
            var problem = CheckConfidence(report);
            if (problem is not null)
            {
                await _runs.FinishAsync(runId, "flagged", report.Sites.Count, 0, problem, ct);
                _log.LogWarning("Report parse rejected: {Problem}", problem);
                return Done(new IngestionResult(runId, "flagged", report.Sites.Count, 0, problem));
            }

            var ids = await _samples.LocationIdsByCityCodeAsync(ct);
            var rows = BuildRows(report, ids, out var incompleteTracked);
            var published = download.PublishedAt ?? DateTime.UtcNow;
            var written = await _samples.UpsertSamplesAsync(documentId, runId, published, rows, ct);

            var status = incompleteTracked.Count > 0 ? "flagged" : "succeeded";
            var note = incompleteTracked.Count > 0
                ? "unreadable rows for tracked sites: " + string.Join(", ", incompleteTracked) : null;
            await _runs.FinishAsync(runId, status, report.Sites.Count, written, note, ct);
            _log.LogInformation("Parsed {Sites} sites, {Tracked} tracked, {Written} samples changed",
                report.Sites.Count, ids.Count, written);
            if (_hook is not null && written > 0) await _hook.AfterRunAsync(source.Name, ct);
            return Done(new IngestionResult(runId, status, report.Sites.Count, written, note));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogError(ex, "Water quality ingestion failed");
            await _runs.FinishAsync(runId, "failed", 0, 0, ex.Message, CancellationToken.None);
            return Done(new IngestionResult(runId, "failed", 0, 0, ex.Message));
        }
    }

    public string? CheckConfidence(ParsedReport report)
    {
        if (report.Sites.Count < _options.MinSites)
            return $"only {report.Sites.Count} site rows parsed, expected at least {_options.MinSites}; the layout may have changed";
        if (report.Confidence < _options.MinConfidence)
            return $"only {report.Confidence:P0} of rows parsed cleanly, need {_options.MinConfidence:P0}";
        return null;
    }

    public static List<WaterQualityRow> BuildRows(ParsedReport report, IReadOnlyDictionary<string, int> idsByCode,
        out List<string> incompleteTracked)
    {
        incompleteTracked = new List<string>();
        var rows = new List<WaterQualityRow>();

        foreach (var site in report.Sites)
        {
            if (site.Code is null || !idsByCode.TryGetValue(site.Code, out var locationId)) continue;
            if (!site.IsComplete)
            {
                incompleteTracked.Add($"{site.Code} ({string.Join("; ", site.Problems)})");
                continue;
            }

            rows.Add(ToRow(locationId, site.LatestDate!.Value, site.Latest!, site.Status));
            // older samples are only known by the week they were reported under, and the column covering
            // the latest sample is that same sample again
            foreach (var h in site.History.Where(h => !ReportParser.IsSameWeek(h.SampledOn, site.LatestDate!.Value)))
                rows.Add(ToRow(locationId, h.SampledOn, h.Value, null));
        }
        return rows;
    }

    private static WaterQualityRow ToRow(int locationId, DateOnly day, SampleValue value, string? rating) =>
        new(locationId, DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc),
            value.Cfu, value.Qualifier, rating);

    private string Store(SourceDocumentDownload download)
    {
        Directory.CreateDirectory(_options.StoragePath);
        var path = Path.Combine(_options.StoragePath, download.Sha256 + ".pdf");
        if (!File.Exists(path)) File.WriteAllBytes(path, download.Content);
        return path;
    }
}
