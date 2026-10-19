using CapeCoast.Core.Domain;
using CapeCoast.Core.Sources;
using CapeCoast.Infrastructure.Resilience;
using Microsoft.Extensions.Options;
using Polly;

namespace CapeCoast.Infrastructure.Sources;

public class OpenMeteoWindSource : IWindSource
{
    public const string SourceName = "open-meteo-forecast";
    public string Name => SourceName;

    private const string Hourly = "wind_speed_10m,wind_gusts_10m,wind_direction_10m";

    private readonly IHttpClientFactory _http;
    private readonly SourceOptions _options;
    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;

    public OpenMeteoWindSource(IHttpClientFactory http, IOptions<SourcesOptions> options)
    {
        _http = http;
        _options = options.Value.OpenMeteoWind;
        _pipeline = ResiliencePipelines.ForHttpSource(_options);
    }

    public async Task<IReadOnlyList<WindObservation>> FetchAsync(IReadOnlyList<Location> locations, CancellationToken ct)
    {
        var url = OpenMeteoRequest.Build(_options, locations, Hourly) + "&wind_speed_unit=kmh";
        var client = _http.CreateClient(Name);
        using var response = await _pipeline.ExecuteAsync(async t => await client.GetAsync(url, t), ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        return Parse(body, locations);
    }

    public static IReadOnlyList<WindObservation> Parse(string json, IReadOnlyList<Location> locations)
    {
        var results = new List<WindObservation>();
        foreach (var (loc, hourly) in OpenMeteoRequest.HourlyBlocks(json, locations))
        {
            var times = hourly.GetProperty("time");
            for (var i = 0; i < times.GetArrayLength(); i++)
            {
                results.Add(new WindObservation(
                    loc.Id,
                    OpenMeteoRequest.ParseTime(times[i].GetString()!),
                    OpenMeteoRequest.At(hourly, "wind_speed_10m", i),
                    OpenMeteoRequest.At(hourly, "wind_gusts_10m", i),
                    OpenMeteoRequest.At(hourly, "wind_direction_10m", i)));
            }
        }
        return results;
    }
}
