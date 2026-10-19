namespace CapeCoast.Core.Domain;

public record MarineObservation(
    int LocationId,
    DateTime ObservedAt,
    double? WaveHeightM,
    double? SwellHeightM,
    double? SwellPeriodS,
    double? SwellDirectionDeg,
    double? WindWaveHeightM,
    double? SeaTempC,
    double? SeaLevelM)
{
    public string[] QualityFlags { get; init; } = Array.Empty<string>();
}

public record WindObservation(
    int LocationId,
    DateTime ObservedAt,
    double? SpeedKmh,
    double? GustKmh,
    double? DirectionDeg)
{
    public string[] QualityFlags { get; init; } = Array.Empty<string>();
}
