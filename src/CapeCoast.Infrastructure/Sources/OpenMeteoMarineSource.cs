using CapeCoast.Core.Domain;
using CapeCoast.Core.Sources;
using CapeCoast.Infrastructure.Resilience;
using Microsoft.Extensions.Options;
using Polly;

namespace CapeCoast.Infrastructure.Sources;

public class OpenMeteoMarineSource : IMarineSource
{
    public const string SourceName = "open-meteo-marine";
    public string Name => SourceName;

    private const string Hourly =
        "wave_height,swell_wave_height,swell_wave_period,swell_wave_direction,wind_wave_height,sea_surface_temperature,sea_level_height_msl";

    private readonly IHttpClientFactory _http;
    private readonly SourceOptions _options;
    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;

    public OpenMeteoMarineSource(IHttpClientFactory http, IOptions<SourcesOptions> options)
    {
        _http = http;
        _options = options.Value.OpenMeteoMarine;
        _pipeline = ResiliencePipelines.ForHttpSource(_options);
    }

    public async Task<IReadOnlyList<MarineObservation>> FetchAsync(IReadOnlyList<Location> locations, CancellationToken ct)
    {
        var url = OpenMeteoRequest.Build(_options, locations, Hourly);
        var client = _http.CreateClient(Name);
        using var response = await _pipeline.ExecuteAsync(async t => await client.GetAsync(url, t), ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        return Parse(body, locations);
    }

    public static IReadOnlyList<MarineObservation> Parse(string json, IReadOnlyList<Location> locations)
    {
        var results = new List<MarineObservation>();
        foreach (var (loc, hourly) in OpenMeteoRequest.HourlyBlocks(json, locations))
        {
            var times = hourly.GetProperty("time");
            for (var i = 0; i < times.GetArrayLength(); i++)
            {
                results.Add(new MarineObservation(
                    loc.Id,
                    OpenMeteoRequest.ParseTime(times[i].GetString()!),
                    OpenMeteoRequest.At(hourly, "wave_height", i),
                    OpenMeteoRequest.At(hourly, "swell_wave_height", i),
                    OpenMeteoRequest.At(hourly, "swell_wave_period", i),
                    OpenMeteoRequest.At(hourly, "swell_wave_direction", i),
                    OpenMeteoRequest.At(hourly, "wind_wave_height", i),
                    OpenMeteoRequest.At(hourly, "sea_surface_temperature", i),
                    OpenMeteoRequest.At(hourly, "sea_level_height_msl", i)));
            }
        }
        return results;
    }
}
