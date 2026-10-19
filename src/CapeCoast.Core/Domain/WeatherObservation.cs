namespace CapeCoast.Core.Domain;

public record WeatherObservation(
    int LocationId,
    DateTime ObservedAt,
    double? AirTempC,
    double? PrecipitationMm,
    double? UvIndex,
    double? VisibilityM,
    double? CloudCoverPct,
    double? HumidityPct)
{
    public string[] QualityFlags { get; init; } = Array.Empty<string>();
}
