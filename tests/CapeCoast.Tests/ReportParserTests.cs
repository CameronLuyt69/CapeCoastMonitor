using CapeCoast.Core.Domain;
using CapeCoast.Infrastructure.Ingestion;
using CapeCoast.Infrastructure.Sources.WaterQuality;

namespace CapeCoast.Tests;

public class ReportParserTests
{
    private static ParsedReport Parse(byte[] pdf) => ReportParser.Parse(PdfWordReader.Read(pdf));

    private static FakeRow Row(int order, string name, string code, string latestDate = "2026-09-08",
        string result = "12", string status = "Green", params string[] history) =>
        new(order, name, code, latestDate, result, status, history.Length == 0 ? new[] { result, "4", "", "7" } : history);

    [Fact]
    public void Reads_site_code_name_coordinates_and_latest_result()
    {
        var report = Parse(SyntheticReport.Single(Row(1, "Clifton Fourth Beach", "CN09", "2026-09-08", "12")));

        var site = Assert.Single(report.Sites);
        Assert.Equal("CN09", site.Code);
        Assert.Equal("Clifton Fourth Beach", site.Name);
        Assert.Equal(new DateOnly(2026, 9, 8), site.LatestDate);
        Assert.Equal(12, site.Latest!.Cfu);
        Assert.Equal("Green", site.Status);
        Assert.Equal(-34.1, site.Latitude);
        Assert.Equal(18.4, site.Longitude);
        Assert.True(site.IsComplete);
    }

    [Fact]
    public void History_cells_are_matched_to_their_date_columns_and_gaps_stay_gaps()
    {
        var report = Parse(SyntheticReport.Single(Row(1, "Camps Bay Central", "CN41", "2026-09-08", "12", "Green", "12", "4", "", "7")));

        var history = report.Sites[0].History;
        Assert.Equal(3, history.Count);
        Assert.Equal(4, history.Single(h => h.SampledOn == new DateOnly(2026, 8, 31)).Value.Cfu);
        Assert.Equal(7, history.Single(h => h.SampledOn == new DateOnly(2026, 8, 17)).Value.Cfu);
        Assert.DoesNotContain(history, h => h.SampledOn == new DateOnly(2026, 8, 24));
    }

    [Fact]
    public void Rows_above_the_table_header_are_ignored()
    {
        var report = Parse(SyntheticReport.Single(Row(1, "Camps Bay Central", "CN41")));

        Assert.Single(report.Sites);
        Assert.DoesNotContain(report.Sites, s => s.Name == "Silwerstroomstrand");
    }

    [Fact]
    public void Rows_on_a_later_page_use_the_header_from_the_page_before()
    {
        var pdf = SyntheticReport.Build(new IReadOnlyList<FakeRow>[]
        {
            new[] { Row(1, "Clifton Fourth Beach", "CN09") },
            new[] { Row(2, "Llandudno", "XCN03") },
        });

        var report = Parse(pdf);

        Assert.Equal(new[] { "CN09", "XCN03" }, report.Sites.Select(s => s.Code));
        Assert.All(report.Sites, s => Assert.NotEmpty(s.History));
    }

    [Theory]
    [InlineData(">2419", ">", 2419)]
    [InlineData("<10", "<", 10)]
    public void Censored_results_keep_their_qualifier(string cell, string qualifier, int cfu)
    {
        var report = Parse(SyntheticReport.Single(Row(1, "Strand Lifesaving Club", "XCS26", "2026-09-09", cell, "Red", cell)));

        var latest = report.Sites[0].Latest!;
        Assert.Equal(qualifier, latest.Qualifier);
        Assert.Equal(cfu, latest.Cfu);
    }

    [Theory]
    [InlineData("ND")]
    [InlineData("TFD")]
    public void Missing_result_markers_become_qualifier_only_values(string marker)
    {
        var report = Parse(SyntheticReport.Single(Row(1, "Noordhoek Beach", "CS40", "2026-09-11", marker, "TFD", marker)));

        var latest = report.Sites[0].Latest!;
        Assert.Null(latest.Cfu);
        Assert.Equal(marker, latest.Qualifier);
    }

    [Fact]
    public void Row_without_a_site_code_is_a_problem_not_a_guess()
    {
        var report = Parse(SyntheticReport.Single(
            Row(1, "Clifton Fourth Beach", "CN09"),
            new FakeRow(2, "Mystery Beach", null, "2026-09-08", "5", "Green", new[] { "5" })));

        var bad = report.Sites.Single(s => s.Name == "Mystery Beach");
        Assert.Contains("no site code", bad.Problems);
        Assert.False(bad.IsComplete);
        Assert.Equal(0.5, report.Confidence);
    }

    [Fact]
    public void History_that_contradicts_the_latest_result_is_flagged()
    {
        // latest sample is 8 Sep, so the 7 Sep column is the same sample and has to say 12
        var report = Parse(SyntheticReport.Single(Row(1, "Clifton Fourth Beach", "CN09", "2026-09-08", "12", "Green", "99")));

        Assert.Contains("history disagrees with the most recent result", report.Sites[0].Problems);
    }

    [Fact]
    public void A_document_without_the_table_parses_to_nothing()
    {
        var report = Parse(SyntheticReport.Build(new IReadOnlyList<FakeRow>[] { Array.Empty<FakeRow>() }));

        Assert.Empty(report.Sites);
        Assert.Equal(0, report.Confidence);
    }

    [Theory]
    [InlineData("12", 12, null)]
    [InlineData(">2419", 2419, ">")]
    [InlineData("<10", 10, "<")]
    [InlineData("ND", null, "ND")]
    [InlineData("TFD", null, "TFD")]
    public void Cell_values_parse(string text, int? cfu, string? qualifier)
    {
        var value = SampleValue.TryParse(text)!;
        Assert.Equal(cfu, value.Cfu);
        Assert.Equal(qualifier, value.Qualifier);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("-5")]
    public void Garbage_cells_do_not_parse(string text) => Assert.Null(SampleValue.TryParse(text));

    [Fact]
    public void Only_tracked_complete_sites_become_rows_and_the_latest_week_is_not_duplicated()
    {
        var report = Parse(SyntheticReport.Single(
            Row(1, "Clifton Fourth Beach", "CN09", "2026-09-08", "12", "Green", "12", "4", "", "7"),
            Row(2, "Somewhere Else", "CS99", "2026-09-08", "3", "Green", "3")));

        var rows = WaterQualityIngestionService.BuildRows(report, new Dictionary<string, int> { ["CN09"] = 1 }, out var bad);

        Assert.Empty(bad);
        Assert.All(rows, r => Assert.Equal(1, r.LocationId));
        Assert.Equal(3, rows.Count);                                    // latest + two older weeks
        Assert.Single(rows, r => r.Rating == "Green");                  // City status belongs to the latest sample only
        Assert.DoesNotContain(rows, r => r.SampledAt == new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc));
    }
}
