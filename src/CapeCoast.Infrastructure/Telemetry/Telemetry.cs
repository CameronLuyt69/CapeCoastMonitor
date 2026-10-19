using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CapeCoast.Infrastructure.Telemetry;

/// <summary>The app's own meters and spans. Names are prefixed ccm. so they are easy to pick out.</summary>
public static class Telemetry
{
    public const string Name = "CapeCoast";

    public static readonly ActivitySource Source = new(Name);
    private static readonly Meter Meter = new(Name);

    public static readonly Histogram<double> RunDuration = Meter.CreateHistogram<double>(
        "ccm.ingestion.run.duration", "s", "How long an ingestion run took, by source and outcome");

    public static readonly Counter<long> Runs = Meter.CreateCounter<long>(
        "ccm.ingestion.runs", description: "Ingestion runs by source and outcome (a failed outcome is a failure)");

    public static readonly Counter<long> RowsFetched = Meter.CreateCounter<long>(
        "ccm.ingestion.rows.fetched", description: "Rows received from a source");

    public static readonly Counter<long> RowsWritten = Meter.CreateCounter<long>(
        "ccm.ingestion.rows.written", description: "Rows inserted or changed in the database");

    public static readonly Histogram<double> JobDuration = Meter.CreateHistogram<double>(
        "ccm.job.duration", "s", "Wall time of a scheduled job, by job and outcome");

    public static readonly Counter<long> AlertEvents = Meter.CreateCounter<long>(
        "ccm.alerts.events", description: "Incidents opened or resolved, by severity");

    public static readonly Counter<long> AlertNotifyFailures = Meter.CreateCounter<long>(
        "ccm.alerts.notify.failures", description: "Alert messages the webhook did not accept");

    public static void RecordRun(string source, string outcome, int fetched, int written, TimeSpan elapsed)
    {
        var src = new KeyValuePair<string, object?>("source", source);
        var result = new KeyValuePair<string, object?>("outcome", outcome);
        RunDuration.Record(elapsed.TotalSeconds, src, result);
        Runs.Add(1, src, result);
        RowsFetched.Add(fetched, src);
        RowsWritten.Add(written, src);
    }

    /// <summary>
    /// Starts a span, or a plain activity when nothing is listening, so there is always a trace id
    /// to use as the correlation id across logs, the audit table and the response headers.
    /// </summary>
    public static Activity StartActivity(string name, ActivityKind kind = ActivityKind.Internal) =>
        Source.StartActivity(name, kind) ?? new Activity(name).Start();
}
