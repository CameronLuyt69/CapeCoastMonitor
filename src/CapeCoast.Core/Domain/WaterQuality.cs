namespace CapeCoast.Core.Domain;

/// <summary>
/// One enterococci cell from the City report. A plain count has a value and no qualifier,
/// a censored count (">2419", "&lt;10") has both, and ND / TFD cells have only a qualifier.
/// </summary>
public record SampleValue(int? Cfu, string? Qualifier)
{
    public static SampleValue? TryParse(string text)
    {
        text = text.Trim();
        if (text is "ND" or "TFD") return new SampleValue(null, text);

        var qualifier = (string?)null;
        if (text.StartsWith('>') || text.StartsWith('<'))
        {
            qualifier = text[..1];
            text = text[1..];
        }
        return int.TryParse(text.Replace(",", ""), out var n) && n >= 0 ? new SampleValue(n, qualifier) : null;
    }
}

public record DatedSample(DateOnly SampledOn, SampleValue Value);

public record ReportSite(
    int Order,
    string Name,
    string? Code,
    double? Longitude,
    double? Latitude,
    DateOnly? LatestDate,
    SampleValue? Latest,
    string? Status,
    IReadOnlyList<DatedSample> History,
    IReadOnlyList<string> Problems)
{
    public bool IsComplete => Problems.Count == 0 && Code is not null && LatestDate is not null && Latest is not null;
}

public record ParsedReport(IReadOnlyList<ReportSite> Sites, IReadOnlyList<string> Problems)
{
    public double Confidence => Sites.Count == 0 ? 0 : (double)Sites.Count(s => s.IsComplete) / Sites.Count;
}

public record SourceDocumentDownload(string Url, byte[] Content, string Sha256, DateTime? PublishedAt);
