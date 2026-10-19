using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace CapeCoast.Tests;

public record FakeRow(
    int Order, string Name, string? Code, string LatestDate, string Result, string Status,
    string[] History, string Lon = "18.4", string Lat = "-34.1");

/// <summary>
/// Builds a small PDF laid out like the City's report table (same columns, same relative positions),
/// so the parser can be tested end to end without committing the City's own document to the repo.
/// </summary>
public static class SyntheticReport
{
    public static readonly string[] HeaderDates = { "2026/09/07", "2026/08/31", "2026/08/24", "2026/08/17" };

    public static byte[] Build(IReadOnlyList<IReadOnlyList<FakeRow>> pages, bool headerOnFirstPageOnly = true)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        for (var p = 0; p < pages.Count; p++)
        {
            var page = builder.AddPage(1754, 1240);
            var y = 1100.0;

            if (p == 0)
            {
                // a blue-flag style table above the real one, which the parser has to ignore
                page.AddText("1", 10, new PdfPoint(61, y), font);
                page.AddText("Silwerstroomstrand", 10, new PdfPoint(154, y), font);
                page.AddText("0", 10, new PdfPoint(350, y), font);
                y -= 120;
            }

            if (p == 0 || !headerOnFirstPageOnly)
            {
                page.AddText("Site", 10, new PdfPoint(38, y), font);
                page.AddText("Order", 10, new PdfPoint(59, y), font);
                for (var i = 0; i < HeaderDates.Length; i++)
                    page.AddText(HeaderDates[i], 10, new PdfPoint(930 + 76 * i, y), font);
                y -= 40;
            }

            foreach (var row in pages[p])
            {
                page.AddText(row.Order.ToString(), 10, new PdfPoint(61, y), font);
                page.AddText(row.Name, 10, new PdfPoint(127, y), font);
                page.AddText("Good", 10, new PdfPoint(328, y), font);
                page.AddText("Good", 10, new PdfPoint(413, y), font);
                page.AddText("Excellent", 10, new PdfPoint(480, y), font);
                page.AddText("97", 10, new PdfPoint(574, y + 1), font);
                page.AddText(row.LatestDate, 10, new PdfPoint(628, y), font);
                page.AddText(row.Result, 10, new PdfPoint(728, y), font);
                page.AddText(row.Status, 10, new PdfPoint(790, y), font);
                page.AddText("2/2/2022", 10, new PdfPoint(860, y), font);

                for (var i = 0; i < row.History.Length; i++)
                    if (row.History[i].Length > 0)
                        page.AddText(row.History[i], 10, new PdfPoint(952 + 76 * i, y), font);

                if (row.Code is not null) page.AddText(row.Code, 10, new PdfPoint(1471, y), font);
                page.AddText(row.Lon, 10, new PdfPoint(1540, y), font);
                page.AddText(row.Lat, 10, new PdfPoint(1610, y), font);
                y -= 15;
            }
        }
        return builder.Build();
    }

    public static byte[] Single(params FakeRow[] rows) => Build(new[] { (IReadOnlyList<FakeRow>)rows });
}
