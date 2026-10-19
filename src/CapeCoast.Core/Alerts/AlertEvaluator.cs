namespace CapeCoast.Core.Alerts;

/// <summary>
/// Decides whether a rule is breached right now. Pure: it is handed the readings and the baseline
/// and touches no database, so every case can be tested directly.
/// </summary>
public static class AlertEvaluator
{
    public static bool Compare(double value, string op, double threshold) => op switch
    {
        ">" => value > threshold,
        ">=" => value >= threshold,
        "<" => value < threshold,
        "<=" => value <= threshold,
        _ => throw new ArgumentException($"unknown operator {op}"),
    };

    /// <param name="readings">Readings up to now in time order, covering at least the rule's duration window.</param>
    /// <param name="baseline">Trailing average for baseline rules, ignored for static ones.</param>
    /// <param name="minBaselineSamples">Fewer baseline samples than this means there is no usable norm yet.</param>
    /// <param name="maxGap">The longest gap that still counts as continuous data when a rule needs a duration.</param>
    public static Verdict Evaluate(AlertRule rule, IReadOnlyList<Reading> readings, Baseline? baseline,
        int minBaselineSamples, TimeSpan maxGap)
    {
        if (readings.Count == 0) return new Verdict(false, null, null, "no data", Inconclusive: true);
        var latest = readings[^1];

        return rule.IsBaseline
            ? EvaluateBaseline(rule, latest, baseline, minBaselineSamples)
            : EvaluateStatic(rule, latest, readings, maxGap);
    }

    private static Verdict EvaluateStatic(AlertRule rule, Reading latest, IReadOnlyList<Reading> readings, TimeSpan maxGap)
    {
        if (!Compare(latest.Value, rule.Operator, rule.Threshold))
            return new Verdict(false, latest.Value, latest.At, "within threshold");

        if (rule.DurationMinutes <= 0)
            return new Verdict(true, latest.Value, latest.At, "threshold crossed");

        var windowStart = latest.At - TimeSpan.FromMinutes(rule.DurationMinutes);
        var window = readings.Where(r => r.At >= windowStart).ToList();

        // The window has to be covered by data, otherwise "for 3 hours" would be claimed from one reading.
        // The window can start between two hourly readings, so allow a shortfall of less than one step (half the gap limit).
        var slack = TimeSpan.FromTicks(maxGap.Ticks / 2);
        if (window.Count == 0 || window[0].At - windowStart >= slack)
            return new Verdict(false, latest.Value, latest.At, "not enough history to cover the duration", Inconclusive: true);

        for (var i = 1; i < window.Count; i++)
            if (window[i].At - window[i - 1].At > maxGap)
                return new Verdict(false, latest.Value, latest.At, "gap in the data inside the duration window", Inconclusive: true);

        return window.All(r => Compare(r.Value, rule.Operator, rule.Threshold))
            ? new Verdict(true, latest.Value, latest.At, $"threshold held for {rule.DurationMinutes} minutes")
            : new Verdict(false, latest.Value, latest.At, "threshold not held for the full duration");
    }

    /// <summary>
    /// The threshold is a percentage. ">" or ">=" means the value is above its trailing norm by more than that,
    /// "&lt;" or "&lt;=" means it is below the norm by more than that.
    /// </summary>
    private static Verdict EvaluateBaseline(AlertRule rule, Reading latest, Baseline? baseline, int minSamples)
    {
        if (baseline?.Mean is null || baseline.Count < minSamples)
            return new Verdict(false, latest.Value, latest.At, $"baseline needs {minSamples} samples, has {baseline?.Count ?? 0}", Inconclusive: true);
        if (baseline.Mean == 0)
            return new Verdict(false, latest.Value, latest.At, "baseline is zero, a percentage deviation is undefined", Inconclusive: true);

        var deviation = (latest.Value - baseline.Mean.Value) / Math.Abs(baseline.Mean.Value) * 100;
        var above = rule.Operator is ">" or ">=";
        var inclusive = rule.Operator is ">=" or "<=";
        var breached = above
            ? (inclusive ? deviation >= rule.Threshold : deviation > rule.Threshold)
            : (inclusive ? deviation <= -rule.Threshold : deviation < -rule.Threshold);

        return new Verdict(breached, latest.Value, latest.At,
            breached ? "deviates from the 30-day norm" : "within the 30-day norm", deviation, baseline.Mean);
    }

    /// <summary>The worst value seen during an incident: highest for ">" rules, lowest for "&lt;" rules.</summary>
    public static double Peak(string op, double? current, double next) =>
        current is null ? next : op is ">" or ">=" ? Math.Max(current.Value, next) : Math.Min(current.Value, next);
}
