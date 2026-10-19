using System.Text.Json;
using CapeCoast.Core.Alerts;
using CapeCoast.Infrastructure.Alerts;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CapeCoast.Tests;

/// <summary>Stands in for n8n: records what it is sent and answers with a status the test chooses.</summary>
public sealed class FakeWebhook : IAsyncDisposable
{
    private readonly WebApplication _app;
    // headers are copied inside the handler, ASP.NET clears the live dictionary once the request ends
    public List<(string Body, Dictionary<string, string> Headers)> Requests { get; } = new();
    public int Status { get; set; } = 200;
    public string Url { get; private set; } = "";

    private FakeWebhook(WebApplication app) => _app = app;

    public static async Task<FakeWebhook> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new[] { "--urls=http://127.0.0.1:0", "--Logging:LogLevel:Default=Warning" });
        var app = builder.Build();
        FakeWebhook? self = null;
        app.MapPost("/webhook/capecoast-alerts", async (HttpContext http) =>
        {
            using var reader = new StreamReader(http.Request.Body);
            var headers = http.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            self!.Requests.Add((await reader.ReadToEndAsync(), headers));
            return Results.StatusCode(self.Status);
        });
        self = new FakeWebhook(app);
        await app.StartAsync();
        self.Url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()
                   + "/webhook/capecoast-alerts";
        return self;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

public class WebhookNotifierTests
{
    private sealed class OneClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static N8nWebhookNotifier Notifier(string? url, string? secret = null) => new(
        new OneClientFactory(),
        Options.Create(new AlertOptions { WebhookUrl = url, WebhookSecret = secret, WebhookRetries = 0, WebhookTimeoutSeconds = 5 }),
        NullLogger<N8nWebhookNotifier>.Instance);

    private static AlertPayload Payload()
    {
        var rule = new AlertRule(3, "Water quality", "enterococci", null, "static", ">", 240, 0, "warning", 60, true);
        var incident = new Incident { Id = 9, OpenedAt = new DateTime(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc), BreachCount = 1, PeakValue = 300 };
        return AlertPayload.Build("opened", incident, rule, new AlertLocation(6, "muizenberg", "Muizenberg", "False Bay"),
            "cfu/100ml", new Verdict(true, 300, new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc), "x"), "http://localhost:8088");
    }

    [Fact]
    public async Task The_payload_is_camelcase_json_with_the_documented_fields()
    {
        await using var hook = await FakeWebhook.StartAsync();

        var sent = await Notifier(hook.Url).SendAsync(Payload(), CancellationToken.None);

        Assert.True(sent);
        var json = JsonDocument.Parse(Assert.Single(hook.Requests).Body).RootElement;
        Assert.Equal(1, json.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("opened", json.GetProperty("event").GetString());
        Assert.Equal("warning", json.GetProperty("severity").GetString());
        Assert.Equal(9, json.GetProperty("incidentId").GetInt64());
        Assert.Equal("muizenberg", json.GetProperty("location").GetProperty("slug").GetString());
        Assert.Equal("enterococci", json.GetProperty("rule").GetProperty("metric").GetString());
        Assert.Equal(300, json.GetProperty("value").GetDouble());
        Assert.Equal("cfu/100ml", json.GetProperty("unit").GetString());
        Assert.Equal("http://localhost:8088/#beach=muizenberg", json.GetProperty("link").GetString());
        Assert.Contains("not a safety tool", json.GetProperty("disclaimer").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("resolvedAt").ValueKind);
    }

    [Fact]
    public async Task A_configured_secret_signs_the_body()
    {
        await using var hook = await FakeWebhook.StartAsync();

        await Notifier(hook.Url, "s3cret").SendAsync(Payload(), CancellationToken.None);

        var (body, headers) = Assert.Single(hook.Requests);
        Assert.Equal("sha256=" + N8nWebhookNotifier.Sign(body, "s3cret"), headers["X-CCM-Signature"]);
    }

    [Fact]
    public async Task Without_a_secret_no_signature_header_is_sent()
    {
        await using var hook = await FakeWebhook.StartAsync();

        await Notifier(hook.Url).SendAsync(Payload(), CancellationToken.None);

        var (_, headers) = Assert.Single(hook.Requests);
        Assert.True(headers.ContainsKey("Content-Type"), "the headers were captured");
        Assert.False(headers.ContainsKey("X-CCM-Signature"));
    }

    [Fact]
    public async Task An_error_from_the_webhook_is_reported_as_not_sent()
    {
        await using var hook = await FakeWebhook.StartAsync();
        hook.Status = 404;

        Assert.False(await Notifier(hook.Url).SendAsync(Payload(), CancellationToken.None));
    }

    [Fact]
    public async Task No_webhook_configured_means_nothing_is_sent_and_it_says_so()
    {
        Assert.False(await Notifier(null).SendAsync(Payload(), CancellationToken.None));
        Assert.False(await Notifier("  ").SendAsync(Payload(), CancellationToken.None));
    }

    [Fact]
    public async Task A_webhook_that_is_down_throws_so_the_caller_retries_later()
    {
        await Assert.ThrowsAnyAsync<Exception>(() =>
            Notifier("http://127.0.0.1:1/webhook/capecoast-alerts").SendAsync(Payload(), CancellationToken.None));
    }
}
