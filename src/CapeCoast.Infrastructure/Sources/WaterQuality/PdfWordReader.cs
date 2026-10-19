using UglyToad.PdfPig;

namespace CapeCoast.Infrastructure.Sources.WaterQuality;

public static class PdfWordReader
{
    public static IReadOnlyList<IReadOnlyList<PdfWord>> Read(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        var pages = new List<IReadOnlyList<PdfWord>>();
        foreach (var page in doc.GetPages())
        {
            pages.Add(page.GetWords()
                .Select(w => new PdfWord(w.Text, w.BoundingBox.Left, w.BoundingBox.Right, w.BoundingBox.Bottom))
                .ToList());
        }
        return pages;
    }
}
