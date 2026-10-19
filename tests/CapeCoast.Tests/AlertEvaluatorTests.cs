using CapeCoast.Core.Alerts;
using CapeCoast.Infrastructure.Alerts;

namespace CapeCoast.Tests;

public class AlertEvaluatorTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Gap = TimeSpan.FromMinutes(120);

    private static AlertRule Rule(string op = ">", double threshold = 2, int duration = 0, string type = "static") =>
        new(1, "test", "wave_height", null, type, op, threshold, duration, "warning", 60, true);

    private static Reading[] Hourly(params double[] values) =>
        values.Select((v, i) => new Reading(Now.AddHours(i - values.Length + 1), v)).ToArray();

    private static Verdict Eval(AlertRule rule, Reading[] readings, Baseline? baseline = null) =>
        AlertEvaluator.Evaluate(rule, readings, baseline, minBaselineSamples: 24, Gap);

    // operators

    [Theory]
    [InlineData(">", 3, 2, true)]
    [InlineData(">", 2, 2, false)]
    [InlineData(">=", 2, 2, true)]
    [InlineData("<", 1, 2, true)]
    [InlineData("<", 2, 2, false)]
    [InlineData("<=", 2, 2, true)]
    public void Operators_compare_as_written(string op, double value, double threshold, bool expected)
    {
        Assert.Equal(expected, AlertEvaluator.Compare(value, op, threshold));
    }

    [Fact]
    public void An_unknown_operator_is_an_error_not_a_silent_false()
    {
        Assert.Throws<ArgumentException>(() => AlertEvaluator.Compare(1, "=", 1));
    }

    // static rules

    [Fact]
    public void A_static_rule_breaches_on_the_latest_reading()
    {
        var verdict = Eval(Rule(">", 2), Hourly(1, 1, 3));

        Assert.True(verdict.Breached);
        Assert.Equal(3, verdict.Value);
        Assert.Equal(Now, verdict.At);
    }

    [Fact]
    public void Only_the_latest_reading_counts_when_there_is_no_duration()
    {
        // it was above the threshold earlier, but it is fine now
        Assert.False(Eval(Rule(">", 2), Hourly(5, 5, 1)).Breached);
    }

    [Fact]
    public void No_readings_means_no_verdict_at_all()
    {
        var verdict = Eval(Rule(), Array.Empty<Reading>());

        Assert.False(verdict.Breached);
        Assert.True(verdict.Inconclusive);
    }

    // durations

    [Fact]
    public void A_duration_rule_needs_the_threshold_held_for_the_whole_window()
    {
        var rule = Rule(">", 2, duration: 180);

        Assert.True(Eval(rule, Hourly(3, 3, 3, 3)).Breached);        // four hourly readings cover three hours
        Assert.False(Eval(rule, Hourly(3, 1, 3, 3)).Breached);       // dipped in the middle
        Assert.False(Eval(rule, Hourly(3, 1, 3, 3)).Inconclusive);   // that is a real "no", not "can't tell"
    }

    [Fact]
    public void A_duration_rule_does_not_claim_a_window_it_has_no_data_for()
    {
        var verdict = Eval(Rule(">", 2, duration: 180), Hourly(3, 3));   // only an hour of history

        Assert.False(verdict.Breached);
        Assert.True(verdict.Inconclusive);
    }

    [Fact]
    public void One_missing_hourly_step_of_history_is_not_enough_to_claim_the_window()
    {
        // two hours asked for, readings at -1h and now: only one hour is actually covered
        var verdict = Eval(Rule(">", 2, duration: 120), Hourly(3, 3));

        Assert.False(verdict.Breached);
        Assert.True(verdict.Inconclusive);
        Assert.True(Eval(Rule(">", 2, duration: 120), Hourly(3, 3, 3)).Breached);
    }

    [Fact]
    public void A_gap_inside_the_window_makes_a_duration_rule_inconclusive()
    {
        var readings = new[]
        {
            // three hours with nothing in between, longer than the two hour limit
            new Reading(Now.AddHours(-3), 3),
            new Reading(Now, 3),
        };

        var verdict = Eval(Rule(">", 2, duration: 180), readings);

        Assert.False(verdict.Breached);
        Assert.True(verdict.Inconclusive);
    }

    [Fact]
    public void A_low_threshold_rule_with_a_duration_works_the_same_way()
    {
        var rule = Rule("<", 15, duration: 120);   // sea temperature below 15 for two hours

        Assert.True(Eval(rule, Hourly(14, 14, 14)).Breached);
        Assert.False(Eval(rule, Hourly(14, 16, 14)).Breached);
    }

    // baseline rules

    [Theory]
    [InlineData(">", 50, 2.0, true)]     // +100% against a norm of 1
    [InlineData(">", 50, 1.4, false)]    // +40%
    [InlineData(">", 50, 0.2, false)]    // far below the norm is not "above"
    [InlineData("<", 50, 0.4, true)]     // -60%
    [InlineData("<", 50, 0.6, false)]    // -40%
    [InlineData("<", 50, 2.0, false)]
    [InlineData(">=", 100, 2.0, true)]   // exactly +100%
    [InlineData(">", 100, 2.0, false)]
    [InlineData("<=", 50, 0.5, true)]    // exactly -50%
    public void A_baseline_rule_measures_the_percentage_distance_from_the_norm(string op, double percent, double latest, bool expected)
    {
        var verdict = Eval(Rule(op, percent, type: "baseline"), Hourly(latest), new Baseline(1.0, 100));

        Assert.Equal(expected, verdict.Breached);
        Assert.Equal(1.0, verdict.BaselineMean);
        Assert.False(verdict.Inconclusive);
    }

    [Fact]
    public void A_baseline_verdict_reports_the_deviation()
    {
        var verdict = Eval(Rule(">", 50, type: "baseline"), Hourly(1.8), new Baseline(1.2, 100));

        Assert.Equal(50, verdict.DeviationPercent!.Value, 6);
    }

    [Fact]
    public void A_baseline_with_too_few_samples_is_inconclusive_rather_than_all_clear()
    {
        var verdict = Eval(Rule(">", 50, type: "baseline"), Hourly(5), new Baseline(1.0, 10));

        Assert.False(verdict.Breached);
        Assert.True(verdict.Inconclusive);
    }

    [Fact]
    public void A_missing_baseline_is_inconclusive()
    {
        Assert.True(Eval(Rule(">", 50, type: "baseline"), Hourly(5), null).Inconclusive);
        Assert.True(Eval(Rule(">", 50, type: "baseline"), Hourly(5), new Baseline(null, 0)).Inconclusive);
    }

    [Fact]
    public void A_zero_baseline_cannot_give_a_percentage()
    {
        var verdict = Eval(Rule(">", 50, type: "baseline"), Hourly(5), new Baseline(0, 100));

        Assert.False(verdict.Breached);
        Assert.True(verdict.Inconclusive);
    }

    [Fact]
    public void A_negative_norm_uses_its_magnitude()
    {
        // sea level proxy of -0.5 m, now -1.0 m: that is 100% further below, not 100% above
        var below = Eval(Rule("<", 50, type: "baseline"), Hourly(-1.0), new Baseline(-0.5, 100));
        var above = Eval(Rule(">", 50, type: "baseline"), Hourly(-1.0), new Baseline(-0.5, 100));

        Assert.True(below.Breached);
        Assert.False(above.Breached);
    }

    // peaks

    [Theory]
    [InlineData(">", null, 3.0, 3.0)]
    [InlineData(">", 3.0, 5.0, 5.0)]
    [InlineData(">", 5.0, 3.0, 5.0)]
    [InlineData("<", 3.0, 1.0, 1.0)]
    [InlineData("<=", 1.0, 3.0, 1.0)]
    public void The_peak_is_the_worst_value_in_the_direction_of_the_rule(string op, double? current, double next, double expected)
    {
        Assert.Equal(expected, AlertEvaluator.Peak(op, current, next));
    }

    // payload

    private static readonly AlertLocation Beach = new(6, "muizenberg", "Muizenberg", "False Bay");

    private static Incident Incident(DateTime? resolvedAt = null) => new()
    {
        Id = 12, RuleId = 1, LocationId = 6, OpenedAt = Now.AddHours(-2), LastSeenAt = Now, ResolvedAt = resolvedAt,
        PeakValue = 310, BreachCount = 3,
    };

    [Fact]
    public void The_opened_payload_carries_what_n8n_needs_to_route_it()
    {
        var rule = new AlertRule(1, "Water quality", "enterococci", null, "static", ">", 240, 0, "critical", 60, true);
        var verdict = new Verdict(true, 300, Now, "threshold crossed");

        var payload = AlertPayload.Build("opened", Incident(), rule, Beach, "cfu/100ml", verdict, "http://localhost:8088/");

        Assert.Equal(1, payload.SchemaVersion);
        Assert.Equal("opened", payload.Event);
        Assert.Equal("critical", payload.Severity);
        Assert.Equal(12, payload.IncidentId);
        Assert.Equal("muizenberg", payload.Location.Slug);
        Assert.Equal("enterococci", payload.Rule.Metric);
        Assert.Equal(300, payload.Value);
        Assert.Equal(310, payload.PeakValue);
        Assert.Equal(3, payload.BreachCount);
        Assert.Equal("http://localhost:8088/#beach=muizenberg", payload.Link);
        Assert.Contains("Muizenberg", payload.Summary);
        Assert.Contains("300", payload.Summary);
        Assert.Contains("not a safety tool", payload.Disclaimer);
        Assert.Null(payload.ResolvedAt);
    }

    [Fact]
    public void The_resolved_payload_says_so_and_has_no_stale_value_claim()
    {
        var rule = new AlertRule(1, "Water quality", "enterococci", null, "static", ">", 240, 0, "warning", 60, true);

        var payload = AlertPayload.Build("resolved", Incident(Now), rule, Beach, "cfu/100ml", null, null);

        Assert.Equal("resolved", payload.Event);
        Assert.Equal(Now, payload.ResolvedAt);
        Assert.StartsWith("Resolved:", payload.Summary);
        Assert.Null(payload.Link);
    }

    [Fact]
    public void A_baseline_payload_includes_the_norm_and_deviation()
    {
        var rule = new AlertRule(1, "Swell vs norm", "swell_height", null, "baseline", ">", 50, 0, "info", 60, true);
        var verdict = new Verdict(true, 3.0, Now, "deviates", DeviationPercent: 100, BaselineMean: 1.5);

        var payload = AlertPayload.Build("opened", Incident(), rule, Beach, "m", verdict, null);

        Assert.Equal(1.5, payload.Baseline!.Mean);
        Assert.Equal(100, payload.Baseline.DeviationPercent);
        Assert.Contains("30-day norm", payload.Summary);
    }

    [Fact]
    public void The_webhook_signature_is_a_stable_hmac_of_the_body()
    {
        var a = N8nWebhookNotifier.Sign("{\"a\":1}", "secret");

        Assert.Equal(a, N8nWebhookNotifier.Sign("{\"a\":1}", "secret"));
        Assert.NotEqual(a, N8nWebhookNotifier.Sign("{\"a\":2}", "secret"));
        Assert.NotEqual(a, N8nWebhookNotifier.Sign("{\"a\":1}", "other"));
        Assert.Equal(64, a.Length);
        Assert.Equal(a, a.ToLowerInvariant());
    }
}
