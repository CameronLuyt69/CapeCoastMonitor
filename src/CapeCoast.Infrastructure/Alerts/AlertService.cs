using CapeCoast.Core.Alerts;
using CapeCoast.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CapeCoast.Infrastructure.Alerts;

public record AlertRunSummary
{
    public int Evaluated { get; set; }
    public int Opened { get; set; }
    public int Updated { get; set; }
    public int Resolved { get; set; }
    public int Suppressed { get; set; }
    public int Skipped { get; set; }
    public int Deferred { get; set; }
    public int Notified { get; set; }
    public int NotifyFailed { get; set; }
}

/// <summary>
/// Turns rule breaches into incidents and messages.
/// One open incident per rule and beach: a repeat breach only updates it, and when the value
/// clears a single "resolved" message goes out. Maintenance windows suppress everything for the
/// beach, and a cooldown stops a flapping value from sending a fresh message every hour.
/// A message that couldn't be delivered is not lost, it is retried on the next evaluation.
/// </summary>
public class AlertService : IPostIngestHook
{
    private readonly AlertRepository _repo;
    private readonly IAlertNotifier _notifier;
    private readonly AlertOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<AlertService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AlertService(AlertRepository repo, IAlertNotifier notifier, IOptions<AlertOptions> options,
        TimeProvider time, ILogger<AlertService> log)
    {
        _repo = repo;
        _notifier = notifier;
        _options = options.Value;
        _time = time;
        _log = log;
    }

    public async Task AfterRunAsync(string source, CancellationToken ct)
    {
        try
        {
            var summary = await EvaluateAllAsync(ct);
            _log.LogInformation("Alerts after {Source}: {Opened} opened, {Updated} updated, {Resolved} resolved, {Suppressed} suppressed, {Deferred} deferred, {Failed} notifications failed",
                source, summary.Opened, summary.Updated, summary.Resolved, summary.Suppressed, summary.Deferred, summary.NotifyFailed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // alerting must never turn a good ingestion run into a failed one
            _log.LogError(ex, "Alert evaluation failed after {Source}", source);
        }
    }

    public async Task<AlertRunSummary> EvaluateAllAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        using var span = Telemetry.Telemetry.Source.StartActivity("alerts.evaluate");
        try
        {
            var summary = new AlertRunSummary();
            var now = _time.GetUtcNow().UtcDateTime;
            var rules = await _repo.EnabledRulesAsync(ct);
            var locations = await _repo.LocationsAsync(ct);

            foreach (var rule in rules)
            {
                var source = SourceFor(rule.Metric);
                if (source is null) continue;

                var targets = rule.LocationId is null ? locations : locations.Where(l => l.Id == rule.LocationId).ToList();
                foreach (var location in targets)
                {
                    try
                    {
                        summary.Evaluated++;
                        await EvaluateOneAsync(rule, source, location, now, summary, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log.LogError(ex, "Evaluating rule {Rule} for {Beach} failed", rule.Id, location.Slug);
                    }
                }
            }

            await CloseOrphansAsync(locations, now, summary, ct);
            await RetryPendingResolutionsAsync(locations, now, summary, ct);
            span?.SetTag("ccm.alerts.opened", summary.Opened);
            span?.SetTag("ccm.alerts.resolved", summary.Resolved);
            return summary;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EvaluateOneAsync(AlertRule rule, MetricSource source, AlertLocation location, DateTime now,
        AlertRunSummary summary, CancellationToken ct)
    {
        if (await _repo.InMaintenanceAsync(location.Id, now, ct))
        {
            summary.Suppressed++;
            return;
        }

        var latest = await _repo.LatestAsync(source, location.Id, now - MaxAge(rule.Metric), now, ct);
        if (latest is null)
        {
            summary.Skipped++;       // no fresh data, so say nothing and change nothing
            return;
        }

        IReadOnlyList<Reading> readings = new[] { latest };
        if (!rule.IsBaseline && rule.DurationMinutes > 0)
            readings = await _repo.ReadingsAsync(source, location.Id, latest.At - TimeSpan.FromMinutes(rule.DurationMinutes), now, ct);

        Baseline? baseline = null;
        if (rule.IsBaseline)
            baseline = await _repo.BaselineAsync(source, location.Id, latest.At - TimeSpan.FromDays(_options.BaselineDays), latest.At, ct);

        var verdict = AlertEvaluator.Evaluate(rule, readings, baseline, MinBaseline(rule.Metric), TimeSpan.FromMinutes(_options.MaxGapMinutes));
        if (verdict.Inconclusive)
        {
            summary.Skipped++;
            return;
        }

        var open = await _repo.OpenIncidentAsync(rule.Id, location.Id, ct);

        if (verdict.Breached)
        {
            var value = verdict.Value!.Value;
            if (open is null)
            {
                var last = await _repo.LastNotificationAsync(rule.Id, location.Id, ct);
                if (last is not null && now - last < TimeSpan.FromMinutes(rule.CooldownMinutes))
                {
                    summary.Deferred++;
                    return;
                }

                var incident = await _repo.OpenAsync(rule.Id, location.Id, value, now, ct);
                if (incident is null) return;
                summary.Opened++;
                Telemetry.Telemetry.AlertEvents.Add(1, new KeyValuePair<string, object?>("event", "opened"), new KeyValuePair<string, object?>("severity", rule.Severity));
                await NotifyOpenedAsync(incident, rule, location, source, verdict, now, summary, ct);
            }
            else
            {
                await _repo.UpdateBreachAsync(open.Id, AlertEvaluator.Peak(rule.Operator, open.PeakValue, value), now, ct);
                summary.Updated++;
                // an opening message that never got through goes out now, but a repeat breach sends nothing new
                if (open.LastNotifiedAt is null)
                    await NotifyOpenedAsync(open, rule, location, source, verdict, now, summary, ct);
            }
        }
        else if (open is not null)
        {
            var closed = await _repo.ResolveAsync(open.Id, now, ct);
            if (closed is null) return;
            summary.Resolved++;
            Telemetry.Telemetry.AlertEvents.Add(1, new KeyValuePair<string, object?>("event", "resolved"), new KeyValuePair<string, object?>("severity", rule.Severity));
            if (closed.LastNotifiedAt is not null)
                await NotifyResolvedAsync(closed, rule, location, source, verdict, now, summary, ct);
        }
    }

    private async Task CloseOrphansAsync(IReadOnlyList<AlertLocation> locations, DateTime now, AlertRunSummary summary, CancellationToken ct)
    {
        // an incident whose rule was switched off (or beach deactivated) would otherwise stay open forever
        var enabled = (await _repo.EnabledRulesAsync(ct)).ToDictionary(r => r.Id);
        var active = locations.ToDictionary(l => l.Id);

        foreach (var incident in await _repo.OpenIncidentsAsync(ct))
        {
            if (enabled.ContainsKey(incident.RuleId) && active.ContainsKey(incident.LocationId)) continue;
            var closed = await _repo.ResolveAsync(incident.Id, now, ct);
            if (closed is null) continue;
            summary.Resolved++;
            if (closed.LastNotifiedAt is null) continue;

            var rule = await _repo.RuleAsync(incident.RuleId, ct);
            var location = locations.FirstOrDefault(l => l.Id == incident.LocationId) ?? await _repo.LocationAsync(incident.LocationId, ct);
            if (rule is not null && location is not null && SourceFor(rule.Metric) is { } source)
                await NotifyResolvedAsync(closed, rule, location, source, null, now, summary, ct);
        }
    }

    private async Task RetryPendingResolutionsAsync(IReadOnlyList<AlertLocation> locations, DateTime now, AlertRunSummary summary, CancellationToken ct)
    {
        foreach (var incident in await _repo.PendingResolutionsAsync(ct))
        {
            var rule = await _repo.RuleAsync(incident.RuleId, ct);
            var location = locations.FirstOrDefault(l => l.Id == incident.LocationId) ?? await _repo.LocationAsync(incident.LocationId, ct);
            if (rule is null || location is null || SourceFor(rule.Metric) is not { } source) continue;
            await NotifyResolvedAsync(incident, rule, location, source, null, now, summary, ct);
        }
    }

    // messages

    private async Task NotifyOpenedAsync(Incident incident, AlertRule rule, AlertLocation location, MetricSource source,
        Verdict verdict, DateTime now, AlertRunSummary summary, CancellationToken ct)
    {
        var payload = AlertPayload.Build("opened", incident, rule, location, source.Unit, verdict, _options.PublicUrl);
        if (await SendAsync(payload, summary, ct)) await _repo.MarkOpenedNotifiedAsync(incident.Id, now, ct);
    }

    private async Task NotifyResolvedAsync(Incident incident, AlertRule rule, AlertLocation location, MetricSource source,
        Verdict? verdict, DateTime now, AlertRunSummary summary, CancellationToken ct)
    {
        var payload = AlertPayload.Build("resolved", incident, rule, location, source.Unit, verdict, _options.PublicUrl);
        if (await SendAsync(payload, summary, ct)) await _repo.MarkResolvedNotifiedAsync(incident.Id, now, ct);
    }

    private async Task<bool> SendAsync(AlertPayload payload, AlertRunSummary summary, CancellationToken ct)
    {
        try
        {
            if (await _notifier.SendAsync(payload, ct))
            {
                summary.Notified++;
                return true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Sending the {Event} alert for incident {Incident} failed", payload.Event, payload.IncidentId);
        }
        summary.NotifyFailed++;
        Telemetry.Telemetry.AlertNotifyFailures.Add(1);
        return false;
    }

    // metric plumbing

    private MetricSource? SourceFor(string metric)
    {
        if (metric == MetricCatalog.Enterococci)
            return new MetricSource("water_quality_samples", "sampled_at", "enterococci_cfu_100ml", "cfu/100ml");
        var def = MetricCatalog.Find(metric);
        return def is null ? null : new MetricSource(def.Table, "observed_at", def.Column, def.Unit);
    }

    private TimeSpan MaxAge(string metric) =>
        metric == MetricCatalog.Enterococci ? TimeSpan.FromDays(_options.MaxSampleAgeDays) : TimeSpan.FromHours(_options.MaxDataAgeHours);

    private int MinBaseline(string metric) =>
        metric == MetricCatalog.Enterococci ? _options.BaselineMinSamplesWaterQuality : _options.BaselineMinSamples;
}
