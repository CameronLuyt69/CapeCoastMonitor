namespace CapeCoast.Api;

public class AuthOptions
{
    public const string Section = "Auth";
    public const string PlaceholderPassword = "change-me";

    // Login stays switched off for a role until its password is set in the environment.
    public string? AdminUsername { get; set; }
    public string? AdminPassword { get; set; }
    public string? ViewerUsername { get; set; }
    public string? ViewerPassword { get; set; }
}

public class RateLimitOptions
{
    public const string Section = "RateLimits";

    public int PublicPerMinute { get; set; } = 120;
    public int LoginPerMinute { get; set; } = 8;
    public int AdminPerMinute { get; set; } = 60;
    public int StreamsPerClient { get; set; } = 4;
}

public class WaterQualityDisplayOptions
{
    public const string Section = "WaterQuality";

    /// <summary>
    /// Single-sample guideline in cfu/100 ml. The City states results below 240 are acceptable for
    /// recreational use (SA Water Quality Guidelines for Coastal Marine Water, Vol 2).
    /// </summary>
    public int SingleSampleThresholdCfu { get; set; } = 240;
}
