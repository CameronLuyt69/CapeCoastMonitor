using System.Globalization;
using System.Text.RegularExpressions;
using CapeCoast.Core.Domain;

namespace CapeCoast.Infrastructure.Sources.WaterQuality;

public record PdfWord(string Text, double Left, double Right, double Bottom)
{
    public double Center => (Left + Right) / 2;
}

/// <summary>
/// Reads the table in the City's Coastal Water Quality Report. The PDF is a printed dashboard, so
/// plain text extraction scrambles the rows; this works from word positions instead. Rows are
/// anchored on the site order number in the left margin, and everything within a few points of
/// that baseline belongs to the row. Anything that doesn't fit is recorded as a problem on the
/// site rather than guessed at, and the caller decides what to do with a low-confidence parse.
/// </summary>
public static class ReportParser
{
    private const double RowTolerance = 7;
    private static readonly Regex DateHeader = new(@"^\d{4}/\d{2}/\d{2}$");
    private static readonly Regex IsoDate = new(@"^\d{4}-\d{2}-\d{2}$");
    private static readonly Regex SiteCode = new(@"^[A-Z]{1,4}\d{2}[A-Z]?$");
    private static readonly Regex Decimal = new(@"^-?\d+\.\d+$");
    private static readonly Regex Category = new(@"^(Excellent|Good|Sufficient|Poor)$");
    private static readonly Regex Cell = new(@"^([<>]?\d+|ND|TFD)$");

    private record DateColumn(double Center, DateOnly Date);

    public static ParsedReport Parse(IReadOnlyList<IReadOnlyList<PdfWord>> pages)
    {
        var sites = new List<ReportSite>();
        var problems = new List<string>();
        IReadOnlyList<DateColumn>? carried = null;

        for (var p = 0; p < pages.Count; p++)
        {
            var words = pages[p];
            var headers = FindHeaders(words);
            var anchors = words
                .Where(w => w.Left < 100 && Regex.IsMatch(w.Text, @"^\d{1,3}$"))
                .OrderByDescending(w => w.Bottom)
                .ToList();

            foreach (var anchor in anchors)
            {
                // the lowest header above the row, otherwise the one carried over from the previous page
                var header = headers.Where(h => h.Y > anchor.Bottom).OrderBy(h => h.Y).FirstOrDefault();
                var columns = header?.Columns ?? carried;
                if (columns is null || columns.Count == 0) continue;

                var rowWords = words
                    .Where(w => !ReferenceEquals(w, anchor) && ReferenceEquals(NearestAnchor(anchors, w), anchor))
                    .OrderBy(w => w.Left)
                    .ToList();
                if (rowWords.Count == 0) continue;

                sites.Add(ParseRow(int.Parse(anchor.Text), rowWords, columns));
            }

            // the last header on a page (lowest) applies to rows continuing on the next page
            var last = headers.OrderBy(h => h.Y).FirstOrDefault();
            if (last is not null) carried = last.Columns;
        }

        if (sites.Count == 0) problems.Add("no site rows found");
        return new ParsedReport(sites, problems);
    }

    private record Header(double Y, IReadOnlyList<DateColumn> Columns);

    private static List<Header> FindHeaders(IReadOnlyList<PdfWord> words)
    {
        var result = new List<Header>();
        foreach (var site in words.Where(w => w.Text == "Site" && w.Left < 100))
        {
            var hasOrder = words.Any(w => w.Text == "Order" && w.Left < 120 && Math.Abs(w.Bottom - site.Bottom) < 4);
            if (!hasOrder) continue;

            var columns = words
                .Where(w => DateHeader.IsMatch(w.Text) && Math.Abs(w.Bottom - site.Bottom) < 12)
                .Select(w => new DateColumn(w.Center, DateOnly.ParseExact(w.Text, "yyyy/MM/dd", CultureInfo.InvariantCulture)))
                .OrderBy(c => c.Center)
                .ToList();
            if (columns.Count >= 3) result.Add(new Header(site.Bottom, columns));
        }
        return result;
    }

    public static bool IsSameWeek(DateOnly columnDate, DateOnly sampleDate) =>
        sampleDate.DayNumber - columnDate.DayNumber is >= 0 and <= 6;

    private static PdfWord? NearestAnchor(List<PdfWord> anchors, PdfWord w)
    {
        var best = anchors.OrderBy(a => Math.Abs(a.Bottom - w.Bottom)).First();
        return Math.Abs(best.Bottom - w.Bottom) <= RowTolerance ? best : null;
    }

    private static ReportSite ParseRow(int order, List<PdfWord> row, IReadOnlyList<DateColumn> columns)
    {
        var problems = new List<string>();

        var code = row.FirstOrDefault(w => SiteCode.IsMatch(w.Text));
        if (code is null) problems.Add("no site code");

        var firstCategory = row.FirstOrDefault(w => Category.IsMatch(w.Text));
        var nameLimit = firstCategory?.Left ?? 320;
        var name = string.Join(" ", row.Where(w => w.Left < nameLimit - 1 && w.Left < 320).Select(w => w.Text));
        if (name.Length == 0) problems.Add("no site name");

        var dateWord = row.FirstOrDefault(w => IsoDate.IsMatch(w.Text));
        DateOnly? latestDate = null;
        if (dateWord is null) problems.Add("no most recent sample date");
        else if (DateOnly.TryParseExact(dateWord.Text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            latestDate = d;
        else problems.Add($"bad sample date {dateWord.Text}");

        SampleValue? latest = null;
        string? status = null;
        PdfWord? afterStatus = null;
        if (dateWord is not null)
        {
            var after = row.Where(w => w.Left > dateWord.Right - 1).OrderBy(w => w.Left).ToList();
            var resultWord = after.FirstOrDefault();
            latest = resultWord is null ? null : SampleValue.TryParse(resultWord.Text);
            if (latest is null) problems.Add("no readable latest result");

            var statusWords = after.Skip(1).TakeWhile(w => Regex.IsMatch(w.Text, @"^[A-Za-z]+$")).ToList();
            status = statusWords.Count == 0 ? null : string.Join(" ", statusWords.Select(w => w.Text));
            afterStatus = statusWords.LastOrDefault() ?? resultWord;
        }

        var history = new List<DatedSample>();
        if (afterStatus is not null)
        {
            var firstColumn = columns[0].Center;
            var spacing = columns.Count > 1 ? columns[1].Center - columns[0].Center : 70;
            var cells = row.Where(w => w.Left > afterStatus.Right
                                       && Cell.IsMatch(w.Text)
                                       && w.Center > firstColumn - spacing / 2
                                       && (code is null || w.Left < code.Left));
            foreach (var cell in cells)
            {
                var column = columns.OrderBy(c => Math.Abs(c.Center - cell.Center)).First();
                if (Math.Abs(column.Center - cell.Center) > spacing / 2)
                {
                    problems.Add($"cell {cell.Text} is not under a date column");
                    continue;
                }
                var value = SampleValue.TryParse(cell.Text)!;
                if (history.Any(h => h.SampledOn == column.Date))
                    problems.Add($"two cells under {column.Date:yyyy-MM-dd}");
                else
                    history.Add(new DatedSample(column.Date, value));
            }

            // History columns are labelled with the Monday of the sampling week, while the most recent
            // date is the actual sample day, so the column covering it is the same sample and must agree.
            if (latestDate is not null && latest is not null)
            {
                var sameWeek = history.FirstOrDefault(h => IsSameWeek(h.SampledOn, latestDate.Value));
                if (sameWeek is not null && sameWeek.Value != latest)
                    problems.Add("history disagrees with the most recent result");
            }
        }

        double? lon = null, lat = null;
        if (code is not null)
        {
            foreach (var w in row.Where(w => w.Left > code.Left && Decimal.IsMatch(w.Text)))
            {
                var v = double.Parse(w.Text, CultureInfo.InvariantCulture);
                var isLat = v < 0;
                var inRange = isLat ? v is >= -36 and <= -32 : v is >= 17 and <= 20;
                if (!inRange) { problems.Add($"coordinate {w.Text} out of range"); continue; }
                if (isLat) lat = v; else lon = v;
            }
        }

        return new ReportSite(order, name, code?.Text, lon, lat, latestDate, latest, status, history, problems);
    }
}
