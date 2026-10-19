using System.Text;

namespace CapeCoast.Api.Endpoints;

public static class StreamEndpoint
{
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(25);

    public static void MapStreamEndpoint(this WebApplication app)
    {
        app.MapGet("/api/stream", Stream)
            .RequireRateLimiting(RateLimits.Stream)
            .WithTags("Public data")
            .WithSummary("Server-Sent Events: an 'update' event whenever an ingestion run writes new rows")
            .WithDescription("Events are low volume (a few per hour). A comment line is sent every 25 seconds to keep proxies from closing the stream. " +
                             "Clients should fall back to polling /api/locations if the stream drops.");
    }

    private static async Task Stream(HttpContext http, IngestionNotifier notifier, CancellationToken ct)
    {
        var response = http.Response;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";

        await Write(response, "retry: 15000\n\nevent: hello\ndata: {\"status\":\"connected\"}\n\n", ct);

        using var subscription = notifier.Subscribe();
        while (!ct.IsCancellationRequested)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(KeepAlive);
            try
            {
                var payload = await subscription.Reader.ReadAsync(wait.Token);
                await Write(response, $"event: update\ndata: {payload}\n\n", ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await Write(response, ": keepalive\n\n", ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static async Task Write(HttpResponse response, string text, CancellationToken ct)
    {
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
        await response.Body.FlushAsync(ct);
    }
}
