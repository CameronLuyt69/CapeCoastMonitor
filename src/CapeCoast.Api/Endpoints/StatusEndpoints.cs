using CapeCoast.Infrastructure.Data;
using CapeCoast.Infrastructure.Presentation;

namespace CapeCoast.Api.Endpoints;

public static class StatusEndpoints
{
    public static void MapStatusEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireRateLimiting(RateLimits.Public).WithTags("Status");

        api.MapGet("/health", Health)
            .WithSummary("Liveness and database connectivity");

        api.MapGet("/status", Status)
            .WithSummary("Per-source freshness and the last run results");
    }

    private static async Task<IResult> Health(ReadRepository repo, CancellationToken ct)
    {
        try
        {
            var ok = await repo.PingAsync(ct);
            return ok ? Results.Ok(new { status = "ok", database = "up" })
                      : Results.Json(new { status = "degraded", database = "down" }, statusCode: 503);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return Results.Json(new { status = "degraded", database = "down" }, statusCode: 503);
        }
    }

    private static async Task<IResult> Status(StatusService status, CancellationToken ct) =>
        Results.Json(await status.BuildAsync(ct));
}
