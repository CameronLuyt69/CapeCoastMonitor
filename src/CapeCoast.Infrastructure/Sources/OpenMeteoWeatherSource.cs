using CapeCoast.Core.Domain;
using CapeCoast.Core.Sources;
using CapeCoast.Infrastructure.Resilience;
using Microsoft.Extensions.Options;
using Polly;

namespace CapeCoast.Infrastructure.Sources;

/// <summary>Air temperature, rain, UV, visibility, cloud and humidity from the Open-Meteo forecast API (model values).</summary>
public class OpenMeteoWeatherSource : IWeatherSource
{
    public const string SourceName = "open-meteo-weather";
    public string Name => SourceName;

    private const string Hourly = "temperature_2m,precipitation,uv_index,visibility,cloud_cover,relative_humidity_2m";

    private readonly IHttpClientFactory _http;
    private readonly SourceOptions _options;
    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;

    public OpenMeteoWeatherSource(IHttpClientFactory http, IOptions<SourcesOptions> options)
    {
        _http = http;
        _options = options.Value.OpenMeteoWeather;
        _pipeline = ResiliencePipelines.ForHttpSource(_options);
    }

    public async Task<IReadOnlyList<WeatherObservation>> FetchAsync(IReadOnlyList<Location> locations, CancellationToken ct)
    {
        var url = OpenMeteoRequest.Build(_options, locations, Hourly);
        var client = _http.CreateClient(Name);
        using var response = await _pipeline.ExecuteAsync(async t => await client.GetAsync(url, t), ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        return Parse(body, locations);
    }

    public static IReadOnlyList<WeatherObservation> Parse(string json, IReadOnlyList<Location> locations)
    {
        var results = new List<WeatherObservation>();
        foreach (var (loc, hourly) in OpenMeteoRequest.HourlyBlocks(json, locations))
        {
            var times = hourly.GetProperty("time");
            for (var i = 0; i < times.GetArrayLength(); i++)
            {
                results.Add(new WeatherObservation(
                    loc.Id,
                    OpenMeteoRequest.ParseTime(times[i].GetString()!),
                    OpenMeteoRequest.At(hourly, "temperature_2m", i),
                    OpenMeteoRequest.At(hourly, "precipitation", i),
                    OpenMeteoRequest.At(hourly, "uv_index", i),
                    OpenMeteoRequest.At(hourly, "visibility", i),
                    OpenMeteoRequest.At(hourly, "cloud_cover", i),
                    OpenMeteoRequest.At(hourly, "relative_humidity_2m", i)));
            }
        }
        return results;
    }
}
