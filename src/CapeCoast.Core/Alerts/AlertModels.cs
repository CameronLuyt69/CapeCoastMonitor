namespace CapeCoast.Core.Alerts;

public record AlertRule(
    int Id, string Name, string Metric, int? LocationId, string RuleType, string Operator,
    double Threshold, int DurationMinutes, string Severity, int CooldownMinutes, bool Enabled)
{
    public bool IsBaseline => RuleType == "baseline";
}

public record Reading(DateTime At, double Value);

public record Baseline(double? Mean, long Count);

/// <summary>
/// Inconclusive means the rule couldn't be judged (no data, a gap, no baseline yet). That is different from
/// "judged and fine": an inconclusive result must leave an open incident alone rather than resolve it.
/// </summary>
public record Verdict(bool Breached, double? Value, DateTime? At, string Reason, double? DeviationPercent = null,
    double? BaselineMean = null, bool Inconclusive = false);

public record AlertLocation(int Id, string Slug, string Name, string Coast);

public class Incident
{
    public long Id { get; set; }
    public int RuleId { get; set; }
    public int LocationId { get; set; }
    public DateTime OpenedAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public DateTime? LastNotifiedAt { get; set; }
    public DateTime? ResolutionNotifiedAt { get; set; }
    public double? PeakValue { get; set; }
    public int BreachCount { get; set; }
}
