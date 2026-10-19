using System.Security.Cryptography;
using CapeCoast.Core.Domain;
using CapeCoast.Core.Sources;
using CapeCoast.Infrastructure.Resilience;
using Microsoft.Extensions.Options;
using Polly;

namespace CapeCoast.Infrastructure.Sources.WaterQuality;

/// <summary>City of Cape Town "Coastal Water Quality Report" PDF.</summary>
public class CityReportSource : IWaterQualitySource
{
    public const string SourceName = "cct-coastal-water-quality";
    public string Name => SourceName;

    private readonly IHttpClientFactory _http;
    private readonly WaterQualityOptions _options;
    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;

    public CityReportSource(IHttpClientFactory http, IOptions<SourcesOptions> options)
    {
        _http = http;
        _options = options.Value.CityWaterQuality;
        _pipeline = ResiliencePipelines.ForHttpSource(_options);
    }

    public async Task<SourceDocumentDownload> DownloadAsync(CancellationToken ct)
    {
        var client = _http.CreateClient(Name);
        using var response = await _pipeline.ExecuteAsync(async t => await client.GetAsync(_options.BaseUrl, t), ct);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length < 5 || bytes[0] != '%' || bytes[1] != 'P' || bytes[2] != 'D' || bytes[3] != 'F')
            throw new InvalidDataException("The response is not a PDF");

        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var published = response.Content.Headers.LastModified?.UtcDateTime;
        return new SourceDocumentDownload(_options.BaseUrl, bytes, sha, published);
    }

    public ParsedReport Parse(byte[] document) => ReportParser.Parse(PdfWordReader.Read(document));
}
