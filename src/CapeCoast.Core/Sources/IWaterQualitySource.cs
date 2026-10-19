using CapeCoast.Core.Domain;

namespace CapeCoast.Core.Sources;

public interface IWaterQualitySource
{
    string Name { get; }
    Task<SourceDocumentDownload> DownloadAsync(CancellationToken ct);
    ParsedReport Parse(byte[] document);
}
