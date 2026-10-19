using System.Diagnostics;
using CapeCoast.Infrastructure.Telemetry;
using Serilog.Context;

namespace CapeCoast.Worker.Jobs;

/// <summary>
/// Runs a job inside its own span and gives every log line it writes the same CorrelationId. The id is the
/// trace id, so it also appears in ingestion_runs.correlation_id and on the spans sent to Seq.
/// </summary>
public static class JobRunner
{
    public static async Task RunAsync(string job, Func<Task> work)
    {
        using var activity = Telemetry.StartActivity($"job {job}");
        activity.SetTag("ccm.job", job);

        using var correlation = LogContext.PushProperty("CorrelationId", activity.TraceId.ToString());
        using var name = LogContext.PushProperty("Job", job);

        var clock = Stopwatch.StartNew();
        var outcome = "ok";
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            outcome = "error";
            activity.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            Telemetry.JobDuration.Record(clock.Elapsed.TotalSeconds,
                new KeyValuePair<string, object?>("job", job), new KeyValuePair<string, object?>("outcome", outcome));
        }
    }
}
