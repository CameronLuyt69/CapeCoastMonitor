using CapeCoast.Core.Domain;

namespace CapeCoast.Core.Sources;

public interface IMarineSource
{
    string Name { get; }
    Task<IReadOnlyList<MarineObservation>> FetchAsync(IReadOnlyList<Location> locations, CancellationToken ct);
}

public interface IWeatherSource
{
    string Name { get; }
    Task<IReadOnlyList<WeatherObservation>> FetchAsync(IReadOnlyList<Location> locations, CancellationToken ct);
}

public interface IWindSource
{
    string Name { get; }
    Task<IReadOnlyList<WindObservation>> FetchAsync(IReadOnlyList<Location> locations, CancellationToken ct);
}
