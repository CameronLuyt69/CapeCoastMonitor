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

public class SourcesOptions
{
    public const string Section = "Sources";
    public SourceOptions OpenMeteoMarine { get; set; } = new() { BaseUrl = "https://marine-api.open-meteo.com/v1/marine" };
    public SourceOptions OpenMeteoWind { get; set; } = new() { BaseUrl = "https://api.open-meteo.com/v1/forecast" };
}
