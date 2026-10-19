namespace CapeCoast.Core.Alerts;

public record PayloadRule(int Id, string Name, string Type, string Metric, string Operator, double Threshold, int DurationMinutes);

public record PayloadLocation(int Id, string Slug, string Name, string Coast);

public record PayloadBaseline(double? Mean, double? DeviationPercent);

/// <summary>
/// What n8n receives. schemaVersion changes only when a field is removed or renamed, new fields can appear
/// at any time, so workflows should ignore what they don't know.
/// </summary>
public record AlertPayload(
    int SchemaVersion,
    string Event,                    // "opened" or "resolved"
    long IncidentId,
    string Severity,
    PayloadRule Rule,
    PayloadLocation Location,
    double? Value,
    string Unit,
    DateTime? ObservedAt,
    DateTime OpenedAt,
    DateTime? ResolvedAt,
    int BreachCount,
    double? PeakValue,
    PayloadBaseline? Baseline,
    string Summary,
    string? Link,
    string Disclaimer)
{
    public const string DisclaimerText = "Portfolio demo, not a safety tool. Forecasts are modelled and water quality results are delayed.";

    public static AlertPayload Build(string eventName, Incident incident, AlertRule rule, AlertLocation location,
        string unit, Verdict? verdict, string? publicUrl)
    {
        var value = verdict?.Value ?? incident.PeakValue;
        var summary = Summarise(eventName, rule, location, unit, value);
        var link = string.IsNullOrWhiteSpace(publicUrl) ? null : $"{publicUrl.TrimEnd('/')}/#beach={location.Slug}";

        return new AlertPayload(
            SchemaVersion: 1,
            Event: eventName,
            IncidentId: incident.Id,
            Severity: rule.Severity,
            Rule: new PayloadRule(rule.Id, rule.Name, rule.RuleType, rule.Metric, rule.Operator, rule.Threshold, rule.DurationMinutes),
            Location: new PayloadLocation(location.Id, location.Slug, location.Name, location.Coast),
            Value: value,
            Unit: unit,
            ObservedAt: verdict?.At,
            OpenedAt: incident.OpenedAt,
            ResolvedAt: incident.ResolvedAt,
            BreachCount: incident.BreachCount,
            PeakValue: incident.PeakValue,
            Baseline: verdict?.BaselineMean is null ? null : new PayloadBaseline(verdict.BaselineMean, verdict.DeviationPercent),
            Summary: summary,
            Link: link,
            Disclaimer: DisclaimerText);
    }

    private static string Summarise(string eventName, AlertRule rule, AlertLocation location, string unit, double? value)
    {
        var shown = value is null ? "no value" : $"{Math.Round(value.Value, 2)} {unit}";
        var what = rule.IsBaseline
            ? $"{rule.Metric} {shown} is {(rule.Operator.StartsWith('>') ? "above" : "below")} its 30-day norm by more than {rule.Threshold}%"
            : $"{rule.Metric} {shown} {rule.Operator} {rule.Threshold}";
        return eventName == "resolved"
            ? $"Resolved: {location.Name}, {rule.Name} ({rule.Severity}) is back within limits"
            : $"{location.Name}: {what} ({rule.Severity}, rule \"{rule.Name}\")";
    }
}

public interface IAlertNotifier
{
    /// <summary>True when the message was handed over. False or an exception means it will be retried on the next evaluation.</summary>
    Task<bool> SendAsync(AlertPayload payload, CancellationToken ct);
}

/// <summary>Called after an ingestion run has finished and its data is committed.</summary>
public interface IPostIngestHook
{
    Task AfterRunAsync(string source, CancellationToken ct);
}
