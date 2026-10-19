namespace CapeCoast.Infrastructure.Alerts;

public class AlertOptions
{
    public const string Section = "Alerts";

    /// <summary>n8n webhook URL. With none set, incidents are still recorded but nothing is sent.</summary>
    public string? WebhookUrl { get; set; }

    /// <summary>When set, each request carries X-CCM-Signature: sha256=HMAC of the body.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>Base address used for the link in messages, e.g. http://localhost:8088</summary>
    public string? PublicUrl { get; set; }

    /// <summary>A water quality sample older than this is no longer treated as current for alerting.</summary>
    public int MaxSampleAgeDays { get; set; } = 14;

    /// <summary>Model data older than this means ingestion has stalled, so rules are skipped rather than judged on old values.</summary>
    public int MaxDataAgeHours { get; set; } = 6;

    public int BaselineDays { get; set; } = 30;
    public int BaselineMinSamples { get; set; } = 24;
    public int BaselineMinSamplesWaterQuality { get; set; } = 3;

    /// <summary>Longest gap in hourly data that still counts as continuous for rules with a duration.</summary>
    public int MaxGapMinutes { get; set; } = 120;

    public int WebhookTimeoutSeconds { get; set; } = 10;
    public int WebhookRetries { get; set; } = 2;
}
