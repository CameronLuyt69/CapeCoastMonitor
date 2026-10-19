using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CapeCoast.Core.Alerts;
using CapeCoast.Infrastructure.Resilience;
using CapeCoast.Infrastructure.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace CapeCoast.Infrastructure.Alerts;

/// <summary>Posts alerts to an n8n webhook, which routes them to email, Telegram, Teams and so on.</summary>
public class N8nWebhookNotifier : IAlertNotifier
{
    public const string ClientName = "alerts-webhook";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _http;
    private readonly AlertOptions _options;
    private readonly ILogger<N8nWebhookNotifier> _log;
    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;

    public N8nWebhookNotifier(IHttpClientFactory http, IOptions<AlertOptions> options, ILogger<N8nWebhookNotifier> log)
    {
        _http = http;
        _options = options.Value;
        _log = log;
        _pipeline = ResiliencePipelines.ForHttpSource(new SourceOptions
        {
            Retries = _options.WebhookRetries,
            TimeoutSeconds = _options.WebhookTimeoutSeconds,
        });
    }

    public async Task<bool> SendAsync(AlertPayload payload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.WebhookUrl))
        {
            _log.LogDebug("No webhook configured, the {Event} alert for incident {Incident} was not sent", payload.Event, payload.IncidentId);
            return false;
        }

        var body = JsonSerializer.Serialize(payload, Json);
        var client = _http.CreateClient(ClientName);

        using var response = await _pipeline.ExecuteAsync(async t =>
        {
            // a request can't be sent twice, so each attempt builds its own
            var request = new HttpRequestMessage(HttpMethod.Post, _options.WebhookUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrEmpty(_options.WebhookSecret))
                request.Headers.Add("X-CCM-Signature", "sha256=" + Sign(body, _options.WebhookSecret));
            return await client.SendAsync(request, t);
        }, ct);

        if (response.IsSuccessStatusCode) return true;

        _log.LogWarning("The webhook answered {Status} for the {Event} alert of incident {Incident}",
            (int)response.StatusCode, payload.Event, payload.IncidentId);
        return false;
    }

    public static string Sign(string body, string secret) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
}
