using CapeCoast.Infrastructure.Data;

namespace CapeCoast.Infrastructure.Ingestion;

public record Freshness(string Source, DateTime? LastSuccessAt, bool Stale, string Reason);

/// <summary>A source counts as stale when it hasn't had a successful run within a few intervals.</summary>
public class FreshnessChecker
{
    private const int MissedIntervalsAllowed = 3;
    private readonly IngestionRunRepository _runs;

    public FreshnessChecker(IngestionRunRepository runs) => _runs = runs;

    public async Task<Freshness> CheckAsync(string source, int intervalMinutes, DateTime now, CancellationToken ct = default)
    {
        var status = await _runs.GetStatusAsync(source, ct);
        return Evaluate(source, status.LastSuccessAt, intervalMinutes, now);
    }

    public static Freshness Evaluate(string source, DateTime? lastSuccess, int intervalMinutes, DateTime now)
    {
        if (lastSuccess is null)
            return new Freshness(source, null, true, "no successful run recorded");

        var limit = TimeSpan.FromMinutes(intervalMinutes * MissedIntervalsAllowed);
        var age = now - lastSuccess.Value;
        return age > limit
            ? new Freshness(source, lastSuccess, true, $"last success {age.TotalMinutes:0} min ago, limit {limit.TotalMinutes:0}")
            : new Freshness(source, lastSuccess, false, "ok");
    }
}
