// Dumps every word on a PDF page with its position, so layouts can be inspected before a parser is written.
// usage: PdfProbe <file.pdf> [page]
//        PdfProbe <file.pdf> --parse     run the water quality report parser and print what it finds
using CapeCoast.Infrastructure.Sources.WaterQuality;
using UglyToad.PdfPig;

if (args.Length > 1 && args[1] == "--parse")
{
    var report = ReportParser.Parse(PdfWordReader.Read(File.ReadAllBytes(args[0])));
    foreach (var s in report.Sites)
    {
        var hist = string.Join(" ", s.History.Select(h => $"{h.SampledOn:MM-dd}={h.Value.Qualifier}{h.Value.Cfu}"));
        Console.WriteLine($"{s.Order,3} {s.Code,-6} {s.Name,-36} {s.LatestDate:yyyy-MM-dd} {s.Latest?.Qualifier}{s.Latest?.Cfu,-5} {s.Status,-10} {s.Longitude} {s.Latitude} [{hist}] {string.Join("; ", s.Problems)}");
    }
    Console.WriteLine($"sites={report.Sites.Count} confidence={report.Confidence:P1} problems={string.Join("; ", report.Problems)}");
    return;
}

var path = args[0];
var pageNumber = args.Length > 1 ? int.Parse(args[1]) : 1;

using var doc = PdfDocument.Open(path);
Console.WriteLine($"pages: {doc.NumberOfPages}");
var page = doc.GetPage(pageNumber);
Console.WriteLine($"page {pageNumber}: {page.Width} x {page.Height}");

foreach (var w in page.GetWords().OrderByDescending(w => w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left))
    Console.WriteLine($"y={w.BoundingBox.Bottom,7:0.0} x={w.BoundingBox.Left,7:0.0}  {w.Text}");
