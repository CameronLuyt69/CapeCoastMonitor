namespace CapeCoast.Infrastructure.Telemetry;

public static class Percentiles
{
    /// <summary>
    /// Estimates a percentile from histogram buckets by interpolating inside the bucket that holds it.
    /// </summary>
    /// <param name="upperBounds">Upper bound of each finite bucket, ascending. The overflow bucket has no bound.</param>
    /// <param name="counts">Count per bucket (not cumulative), one longer than upperBounds for the overflow bucket.</param>
    /// <param name="p">0 to 1</param>
    /// <param name="min">Smallest recorded value, if the SDK tracked it. Tightens the first bucket.</param>
    /// <param name="max">Largest recorded value, if tracked. Bounds the overflow bucket and the last estimate.</param>
    public static double? Estimate(IReadOnlyList<double> upperBounds, IReadOnlyList<long> counts, double p, double? min, double? max)
    {
        if (counts.Count != upperBounds.Count + 1) throw new ArgumentException("counts needs one more entry than upperBounds");
        var total = counts.Sum();
        if (total == 0) return null;

        var target = Math.Clamp(p, 0, 1) * total;
        long cumulative = 0;

        for (var i = 0; i < counts.Count; i++)
        {
            var before = cumulative;
            cumulative += counts[i];
            if (counts[i] == 0 || cumulative < target) continue;

            var lower = i == 0 ? Math.Min(0, upperBounds.Count > 0 ? upperBounds[0] : 0) : upperBounds[i - 1];
            var upper = i < upperBounds.Count ? upperBounds[i] : max ?? upperBounds[^1];

            // the recorded extremes are tighter than the bucket edges
            if (min is not null) lower = Math.Max(lower, min.Value);
            if (max is not null) upper = Math.Min(upper, max.Value);
            if (upper < lower) upper = lower;

            var fraction = (target - before) / counts[i];
            return lower + (upper - lower) * fraction;
        }

        return max ?? upperBounds[^1];
    }
}
