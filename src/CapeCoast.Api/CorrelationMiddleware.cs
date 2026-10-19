using System.Diagnostics;
using System.Text.RegularExpressions;
using Serilog.Context;

namespace CapeCoast.Api;

public static partial class CorrelationMiddleware
{
    public const string Header = "X-Correlation-Id";

    // whatever a client sends ends up in logs, so only short, plain identifiers are accepted
    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex Safe();

    public static string Resolve(string? incoming, string? traceId) =>
        incoming is not null && Safe().IsMatch(incoming) ? incoming : traceId ?? Guid.NewGuid().ToString("N");

    /// <summary>
    /// Every request gets a correlation id: the caller's own X-Correlation-Id if it is well-formed, otherwise the
    /// trace id. It is returned in the response header, tagged on the span and added to every log line.
    /// </summary>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var id = Resolve(context.Request.Headers[Header].FirstOrDefault(), Activity.Current?.TraceId.ToString());
        context.Response.Headers[Header] = id;
        Activity.Current?.SetTag("correlation.id", id);

        using (LogContext.PushProperty("CorrelationId", id))
            await next();
    });
}
