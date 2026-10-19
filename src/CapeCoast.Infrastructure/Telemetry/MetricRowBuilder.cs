using System.Text.Json;

namespace CapeCoast.Infrastructure.Telemetry;

public record HistogramSnapshot(
    string Name, IReadOnlyDictionary<string, string> Tags, long Count, double Sum, double? Min, double? Max,
    double[] UpperBounds, long[] Counts);

public record MetricRow(DateTime Time, string Name, string AttributesJson, double Value);

/// <summary>
/// Turns histogram and counter readings into the flat rows stored in metric_points. Kept apart from the
/// OpenTelemetry types so the arithmetic can be tested directly.
/// </summary>
public static class MetricRowBuilder
{
    public const string HttpServerDuration = "http.server.request.duration";

    private static readonly double[] Quantiles = { 0.5, 0.95, 0.99 };
    private static readonly string[] QuantileNames = { "p50", "p95", "p99" };

    public static string Json(IReadOnlyDictionary<string, string> attributes) => JsonSerializer.Serialize(attributes);

    public static MetricRow Counter(DateTime time, string name, IReadOnlyDictionary<string, string> tags, double value) =>
        new(time, name, Json(tags), value);

    /// <param name="excludedRoutes">Routes left out of the all-routes series, e.g. the long-lived event stream.</param>
    public static List<MetricRow> Histograms(DateTime time, IEnumerable<HistogramSnapshot> snapshots, IReadOnlyCollection<string> excludedRoutes)
    {
        var rows = new List<MetricRow>();

        foreach (var metric in snapshots.GroupBy(s => s.Name))
        {
            var isHttp = metric.Key == HttpServerDuration;

            // requests are summarised per route (methods and status codes merged), everything else per tag set
            var groups = metric.GroupBy(s => isHttp
                ? Key(new Dictionary<string, string> { ["route"] = s.Tags.GetValueOrDefault("http.route") ?? "(none)" })
                : Key(s.Tags));

            foreach (var group in groups)
            {
                var attributes = isHttp
                    ? new Dictionary<string, string> { ["route"] = group.First().Tags.GetValueOrDefault("http.route") ?? "(none)" }
                    : (IReadOnlyDictionary<string, string>)group.First().Tags;
                rows.AddRange(Summarise(time, metric.Key, attributes, group));
            }

            if (isHttp)
            {
                var kept = metric.Where(s => !excludedRoutes.Contains(s.Tags.GetValueOrDefault("http.route") ?? "(none)")).ToList();
                if (kept.Count > 0)
                    rows.AddRange(Summarise(time, metric.Key, new Dictionary<string, string> { ["scope"] = "all" }, kept));
            }
        }
        return rows;
    }

    private static string Key(IReadOnlyDictionary<string, string> tags) =>
        string.Join("|", tags.OrderBy(t => t.Key).Select(t => $"{t.Key}={t.Value}"));

    private static IEnumerable<MetricRow> Summarise(DateTime time, string name, IReadOnlyDictionary<string, string> attributes,
        IEnumerable<HistogramSnapshot> snapshots)
    {
        var list = snapshots.ToList();

        // only snapshots that share bucket bounds can be added together
        var bounds = list[0].UpperBounds;
        var compatible = list.Where(s => s.UpperBounds.SequenceEqual(bounds)).ToList();

        var counts = new long[bounds.Length + 1];
        foreach (var s in compatible)
            for (var i = 0; i < counts.Length; i++) counts[i] += s.Counts[i];

        var count = compatible.Sum(s => s.Count);
        var sum = compatible.Sum(s => s.Sum);
        var min = compatible.Where(s => s.Min is not null).Select(s => s.Min).DefaultIfEmpty().Min();
        var max = compatible.Where(s => s.Max is not null).Select(s => s.Max).DefaultIfEmpty().Max();
        var json = Json(attributes);

        yield return new MetricRow(time, name + ".count", json, count);
        yield return new MetricRow(time, name + ".sum", json, sum);
        if (max is not null) yield return new MetricRow(time, name + ".max", json, max.Value);

        for (var i = 0; i < Quantiles.Length; i++)
        {
            var value = Percentiles.Estimate(bounds, counts, Quantiles[i], min, max);
            if (value is not null) yield return new MetricRow(time, $"{name}.{QuantileNames[i]}", json, value.Value);
        }
    }
}
