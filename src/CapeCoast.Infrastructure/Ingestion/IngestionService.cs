using System.Diagnostics;
using CapeCoast.Core.Alerts;
using CapeCoast.Core.Domain;
using CapeCoast.Core.Sources;
using CapeCoast.Infrastructure.Data;
using Microsoft.Extensions.Logging;

namespace CapeCoast.Infrastructure.Ingestion;

public record IngestionResult(long RunId, string Status, int Fetched, int Written, string? Error);

/// <summary>
/// Runs one source end to end: audit row, fetch, validate, upsert, audit row again.
/// Errors are caught and recorded so one failing source never takes down the others.
/// </summary>
public class IngestionService
{
    private readonly LocationRepository _locations;
    private readonly ObservationRepository _observations;
    private readonly IngestionRunRepository _runs;
    private readonly ILogger<IngestionService> _log;
    private readonly IPostIngestHook? _hook;

    public IngestionService(LocationRepository locations, ObservationRepository observations,
        IngestionRunRepository runs, ILogger<IngestionService> log, IPostIngestHook? hook = null)
    {
        _hook = hook;
        _locations = locations;
        _observations = observations;
        _runs = runs;
        _log = log;
    }

    public Task<IngestionResult> RunMarineAsync(IMarineSource source, CancellationToken ct) =>
        RunAsync(source.Name, async (locs, runId, issuedAt, token) =>
        {
            var fetched = await source.FetchAsync(locs, token);
            var valid = fetched.Select(ObservationValidator.Validate).ToList();
            var written = await _observations.UpsertMarineAsync(source.Name, issuedAt, runId, valid, token);
            return (fetched.Count, written, CountFlagged(valid.Select(v => v.QualityFlags)));
        }, ct);

    public Task<IngestionResult> RunWindAsync(IWindSource source, CancellationToken ct) =>
        RunAsync(source.Name, async (locs, runId, issuedAt, token) =>
        {
            var fetched = await source.FetchAsync(locs, token);
            var valid = fetched.Select(ObservationValidator.Validate).ToList();
            var written = await _observations.UpsertWindAsync(source.Name, issuedAt, runId, valid, token);
            return (fetched.Count, written, CountFlagged(valid.Select(v => v.QualityFlags)));
        }, ct);

    public Task<IngestionResult> RunWeatherAsync(IWeatherSource source, CancellationToken ct) =>
        RunAsync(source.Name, async (locs, runId, issuedAt, token) =>
        {
            var fetched = await source.FetchAsync(locs, token);
            var valid = fetched.Select(ObservationValidator.Validate).ToList();
            var written = await _observations.UpsertWeatherAsync(source.Name, issuedAt, runId, valid, token);
            return (fetched.Count, written, CountFlagged(valid.Select(v => v.QualityFlags)));
        }, ct);

    private static int CountFlagged(IEnumerable<string[]> flags) => flags.Count(f => f.Length > 0);

    private async Task<IngestionResult> RunAsync(string source,
        Func<IReadOnlyList<Location>, long, DateTime, CancellationToken, Task<(int Fetched, int Written, int Flagged)>> work,
        CancellationToken ct)
    {
        var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        var runId = await _runs.StartAsync(source, correlationId, ct);
        using var span = Telemetry.Telemetry.Source.StartActivity("ingestion.run");
        span?.SetTag("ccm.source", source);
        span?.SetTag("ccm.run_id", runId);
        var clock = Stopwatch.StartNew();
        using var scope = _log.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["Source"] = source,
            ["RunId"] = runId
        });

        try
        {
            var locations = await _locations.GetActiveAsync(ct);
            var (fetched, written, flagged) = await work(locations, runId, DateTime.UtcNow, ct);

            // rows with missing or out-of-range values are kept (with flags) but the run is marked
            var status = flagged > 0 ? "flagged" : "succeeded";
            var note = flagged > 0 ? $"{flagged} rows carry quality flags" : null;
            await _runs.FinishAsync(runId, status, fetched, written, note, ct);
            _log.LogInformation("Ingested {Fetched} rows, {Written} changed, {Flagged} flagged", fetched, written, flagged);
            Telemetry.Telemetry.RecordRun(source, status, fetched, written, clock.Elapsed);
            span?.SetTag("ccm.rows_written", written);
            if (_hook is not null && written > 0) await _hook.AfterRunAsync(source, ct);
            return new IngestionResult(runId, status, fetched, written, note);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogError(ex, "Ingestion failed");
            await _runs.FinishAsync(runId, "failed", 0, 0, ex.Message, CancellationToken.None);
            Telemetry.Telemetry.RecordRun(source, "failed", 0, 0, clock.Elapsed);
            span?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return new IngestionResult(runId, "failed", 0, 0, ex.Message);
        }
    }
}
