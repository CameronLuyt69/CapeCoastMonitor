using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace CapeCoast.Infrastructure.Telemetry;

/// <summary>
/// Writes OpenTelemetry metrics into the metric_points hypertable so Grafana can chart them from the same
/// Postgres as everything else. Counters become one row per tag set. Histograms become count, sum, max and
/// p50/p95/p99 rows, computed per export interval from the bucket counts.
/// Meant to be used with delta temporality, so each export holds only what happened since the last one.
/// </summary>
public class PostgresMetricExporter : BaseExporter<Metric>
{
    private readonly NpgsqlDataSource _db;
    private readonly string _service;
    private readonly ILogger? _log;
    private readonly IReadOnlyCollection<string> _excludedRoutes;

    public PostgresMetricExporter(NpgsqlDataSource db, string service, IReadOnlyCollection<string>? excludedRoutes = null, ILogger? log = null)
    {
        _db = db;
        _service = service;
        _excludedRoutes = excludedRoutes ?? Array.Empty<string>();
        _log = log;
    }

    /// <summary>Only the app's own metrics and request latency are kept, the runtime's many other meters are not.</summary>
    public static bool IsWanted(string name) =>
        name.StartsWith("ccm.", StringComparison.Ordinal) || name == MetricRowBuilder.HttpServerDuration;

    public override ExportResult Export(in Batch<Metric> batch)
    {
        try
        {
            var rows = new List<MetricRow>();
            var histograms = new List<HistogramSnapshot>();
            var time = DateTime.UtcNow;

            foreach (var metric in batch)
            {
                if (!IsWanted(metric.Name)) continue;

                foreach (ref readonly var point in metric.GetMetricPoints())
                {
                    var tags = new Dictionary<string, string>();
                    foreach (var tag in point.Tags) tags[tag.Key] = tag.Value?.ToString() ?? "";
                    var at = point.EndTime.UtcDateTime;

                    switch (metric.MetricType)
                    {
                        case MetricType.LongSum:
                            rows.Add(MetricRowBuilder.Counter(at, metric.Name, tags, point.GetSumLong()));
                            break;
                        case MetricType.DoubleSum:
                            rows.Add(MetricRowBuilder.Counter(at, metric.Name, tags, point.GetSumDouble()));
                            break;
                        case MetricType.Histogram:
                            histograms.Add(Snapshot(metric.Name, tags, point));
                            time = at;
                            break;
                    }
                }
            }

            rows.AddRange(MetricRowBuilder.Histograms(time, histograms, _excludedRoutes));
            if (rows.Count > 0) Write(rows);
            return ExportResult.Success;
        }
        catch (Exception ex)
        {
            // losing a metrics batch must never take the app down
            _log?.LogWarning(ex, "Could not write metrics");
            return ExportResult.Failure;
        }
    }

    private static HistogramSnapshot Snapshot(string name, Dictionary<string, string> tags, in MetricPoint point)
    {
        var bounds = new List<double>();
        var counts = new List<long>();
        foreach (var bucket in point.GetHistogramBuckets())
        {
            if (!double.IsPositiveInfinity(bucket.ExplicitBound)) bounds.Add(bucket.ExplicitBound);
            counts.Add(bucket.BucketCount);
        }

        double? min = null, max = null;
        if (point.TryGetHistogramMinMaxValues(out var lo, out var hi)) { min = lo; max = hi; }

        return new HistogramSnapshot(name, tags, point.GetHistogramCount(), point.GetHistogramSum(), min, max,
            bounds.ToArray(), counts.ToArray());
    }

    private void Write(List<MetricRow> rows)
    {
        using var conn = _db.OpenConnection();
        using var cmd = new NpgsqlCommand(@"
            INSERT INTO metric_points (time, service, name, attributes, value)
            SELECT t, @service, n, a, v FROM unnest(@times, @names, @attrs, @values) AS x(t, n, a, v)", conn);

        cmd.Parameters.AddWithValue("service", _service);
        cmd.Parameters.Add(new NpgsqlParameter("times", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = rows.Select(r => r.Time).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("names", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = rows.Select(r => r.Name).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("attrs", NpgsqlDbType.Array | NpgsqlDbType.Jsonb) { Value = rows.Select(r => r.AttributesJson).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("values", NpgsqlDbType.Array | NpgsqlDbType.Double) { Value = rows.Select(r => r.Value).ToArray() });
        cmd.ExecuteNonQuery();
    }
}
