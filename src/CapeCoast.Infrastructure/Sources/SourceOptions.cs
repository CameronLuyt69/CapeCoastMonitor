namespace CapeCoast.Infrastructure.Sources;

public class SourceOptions
{
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 20;
    public int Retries { get; set; } = 3;
    public int ForecastDays { get; set; } = 3;
    public int PastDays { get; set; } = 1;
    public int IntervalMinutes { get; set; } = 60;
}

public class WaterQualityOptions : SourceOptions
{
    /// <summary>Where downloaded PDFs are kept so every sample can be traced to its document.</summary>
    public string StoragePath { get; set; } = "data/source-documents";

    /// <summary>Fewer site rows than this means the layout probably changed.</summary>
    public int MinSites { get; set; } = 30;

    /// <summary>Share of site rows that must parse cleanly before anything is written.</summary>
    public double MinConfidence { get; set; } = 0.9;
}

public class SourcesOptions
{
    public const string Section = "Sources";
    public SourceOptions OpenMeteoMarine { get; set; } = new() { BaseUrl = "https://marine-api.open-meteo.com/v1/marine" };
    public SourceOptions OpenMeteoWind { get; set; } = new() { BaseUrl = "https://api.open-meteo.com/v1/forecast" };
    public SourceOptions OpenMeteoWeather { get; set; } = new() { BaseUrl = "https://api.open-meteo.com/v1/forecast" };

    public WaterQualityOptions CityWaterQuality { get; set; } = new()
    {
        BaseUrl = "https://www.capetown.gov.za/_documents/resource.capetown.gov.za/documentcentre/Documents/City%20research%20reports%20and%20review/Coastal_Water_Quality_Report.pdf",
        TimeoutSeconds = 60,
        IntervalMinutes = 1440,
    };
}
