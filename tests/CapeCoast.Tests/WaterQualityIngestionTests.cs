using System.Security.Cryptography;
using CapeCoast.Core.Domain;
using CapeCoast.Core.Sources;
using CapeCoast.Infrastructure.Data;
using CapeCoast.Infrastructure.Ingestion;
using CapeCoast.Infrastructure.Sources;
using CapeCoast.Infrastructure.Sources.WaterQuality;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CapeCoast.Tests;

[Collection("db")]
public class WaterQualityIngestionTests
{
    private readonly DatabaseFixture _fx;

    public WaterQualityIngestionTests(DatabaseFixture fx) => _fx = fx;

    private class FakeSource : IWaterQualitySource
    {
        private readonly byte[] _pdf;
        public FakeSource(byte[] pdf) => _pdf = pdf;
        public string Name => "fake-water-quality";

        public Task<SourceDocumentDownload> DownloadAsync(CancellationToken ct) =>
            Task.FromResult(new SourceDocumentDownload("https://example.test/report.pdf", _pdf,
                Convert.ToHexString(SHA256.HashData(_pdf)).ToLowerInvariant(), new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc)));

        public ParsedReport Parse(byte[] document) => ReportParser.Parse(PdfWordReader.Read(document));
    }

    private WaterQualityIngestionService Service() => new(
        new WaterQualityRepository(_fx.Db), new IngestionRunRepository(_fx.Db),
        Options.Create(new SourcesOptions
        {
            CityWaterQuality = new WaterQualityOptions
            {
                StoragePath = Path.Combine(Path.GetTempPath(), "ccm-tests-" + Guid.NewGuid().ToString("N")),
                MinSites = 2,
            }
        }),
        NullLogger<WaterQualityIngestionService>.Instance);

    private static FakeRow Row(int order, string name, string code, string result, string date = "2026-09-08") =>
        new(order, name, code, date, result, "Green", new[] { result, "4", "", "7" });

    private async Task Reset()
    {
        await using var conn = await _fx.Db.OpenConnectionAsync();
        await conn.ExecuteAsync("DELETE FROM water_quality_samples");
        await conn.ExecuteAsync("DELETE FROM source_documents WHERE source = 'fake-water-quality'");
    }

    private async Task<long> Samples()
    {
        await using var conn = await _fx.Db.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM water_quality_samples");
    }

    [Fact]
    public async Task First_run_stores_the_document_and_the_samples_for_tracked_beaches()
    {
        await Reset();
        var pdf = SyntheticReport.Single(
            Row(1, "Clifton Fourth Beach", "CN09", "12"),
            Row(2, "Muizenberg Surfers Corner", "CS34", ">2419"),
            Row(3, "Untracked Beach", "CS99", "3"));

        var result = await Service().RunAsync(new FakeSource(pdf), CancellationToken.None);

        Assert.Equal("succeeded", result.Status);
        Assert.Equal(3, result.Fetched);
        Assert.Equal(6, await Samples());   // two tracked beaches x (latest + 2 older weeks)

        await using var conn = await _fx.Db.OpenConnectionAsync();
        var latest = await conn.QuerySingleAsync<(int? Cfu, string? Qualifier, string? Rating, string? Url)>(@"
            SELECT s.enterococci_cfu_100ml, s.qualifier, s.rating, d.url
            FROM water_quality_samples s
            JOIN locations l ON l.id = s.location_id
            JOIN source_documents d ON d.id = s.source_document_id
            WHERE l.city_site_code = 'CS34' AND s.sampled_at = '2026-09-08'");
        Assert.Equal(2419, latest.Cfu);
        Assert.Equal(">", latest.Qualifier);
        Assert.Equal("Green", latest.Rating);
        Assert.Equal("https://example.test/report.pdf", latest.Url);
    }

    [Fact]
    public async Task The_same_document_twice_is_recognised_by_its_hash()
    {
        await Reset();
        var source = new FakeSource(SyntheticReport.Single(
            Row(1, "Clifton Fourth Beach", "CN09", "12"), Row(2, "Llandudno", "XCN03", "5")));
        var service = Service();

        await service.RunAsync(source, CancellationToken.None);
        var before = await Samples();
        var second = await service.RunAsync(source, CancellationToken.None);

        Assert.Equal("succeeded", second.Status);
        Assert.Equal(0, second.Written);
        Assert.Equal(before, await Samples());
    }

    [Fact]
    public async Task A_new_document_with_identical_results_changes_no_samples()
    {
        await Reset();
        var rows = new[] { Row(1, "Clifton Fourth Beach", "CN09", "12"), Row(2, "Llandudno", "XCN03", "5") };
        var service = Service();
        await service.RunAsync(new FakeSource(SyntheticReport.Single(rows)), CancellationToken.None);

        // a different file (extra untracked row changes the bytes) carrying the same results
        var again = SyntheticReport.Single(rows.Append(Row(3, "Untracked Beach", "CS99", "1")).ToArray());
        var result = await service.RunAsync(new FakeSource(again), CancellationToken.None);

        Assert.Equal("succeeded", result.Status);
        Assert.Equal(0, result.Written);
    }

    [Fact]
    public async Task A_corrected_result_in_a_new_document_updates_the_sample()
    {
        await Reset();
        var service = Service();
        await service.RunAsync(new FakeSource(SyntheticReport.Single(
            Row(1, "Clifton Fourth Beach", "CN09", "12"), Row(2, "Llandudno", "XCN03", "5"))), CancellationToken.None);

        var corrected = await service.RunAsync(new FakeSource(SyntheticReport.Single(
            Row(1, "Clifton Fourth Beach", "CN09", "300"), Row(2, "Llandudno", "XCN03", "5"))), CancellationToken.None);

        Assert.True(corrected.Written >= 1);
        await using var conn = await _fx.Db.OpenConnectionAsync();
        var cfu = await conn.ExecuteScalarAsync<int>(@"
            SELECT s.enterococci_cfu_100ml FROM water_quality_samples s JOIN locations l ON l.id = s.location_id
            WHERE l.city_site_code = 'CN09' AND s.sampled_at = '2026-09-08'");
        Assert.Equal(300, cfu);
    }

    [Fact]
    public async Task A_layout_that_does_not_parse_flags_the_run_and_writes_nothing()
    {
        await Reset();
        // only one site row where the options demand at least two
        var pdf = SyntheticReport.Single(Row(1, "Clifton Fourth Beach", "CN09", "12"));

        var result = await Service().RunAsync(new FakeSource(pdf), CancellationToken.None);

        Assert.Equal("flagged", result.Status);
        Assert.Contains("layout may have changed", result.Error);
        Assert.Equal(0, await Samples());

        // the document is still kept, so the flagged run can be investigated
        await using var conn = await _fx.Db.OpenConnectionAsync();
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM source_documents WHERE source = 'fake-water-quality'"));
    }

    [Fact]
    public async Task Low_row_confidence_flags_the_run()
    {
        await Reset();
        var pdf = SyntheticReport.Single(
            Row(1, "Clifton Fourth Beach", "CN09", "12"),
            new FakeRow(2, "No Code Beach", null, "2026-09-08", "5", "Green", new[] { "5" }),
            new FakeRow(3, "Also No Code", null, "2026-09-08", "5", "Green", new[] { "5" }));

        var result = await Service().RunAsync(new FakeSource(pdf), CancellationToken.None);

        Assert.Equal("flagged", result.Status);
        Assert.Equal(0, await Samples());
    }

    [Fact]
    public async Task A_download_failure_is_recorded_as_a_failed_run()
    {
        await Reset();
        var result = await Service().RunAsync(new ThrowingSource(), CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Contains("not a PDF", result.Error);
    }

    private class ThrowingSource : IWaterQualitySource
    {
        public string Name => "fake-water-quality";
        public Task<SourceDocumentDownload> DownloadAsync(CancellationToken ct) => throw new InvalidDataException("The response is not a PDF");
        public ParsedReport Parse(byte[] document) => throw new NotSupportedException();
    }
}
