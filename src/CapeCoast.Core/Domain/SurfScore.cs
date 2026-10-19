namespace CapeCoast.Core.Domain;

/// <param name="SwellFromDeg">Direction the swell comes from, in degrees (the marine model's convention).</param>
/// <param name="WindFromDeg">Direction the wind blows from, in degrees.</param>
/// <param name="FacingDeg">The compass bearing a person on the beach looks along to face the open sea.</param>
public record SurfInputs(double? SwellHeightM, double? SwellPeriodS, double? SwellFromDeg,
    double? WindSpeedKmh, double? WindFromDeg, double? FacingDeg);

public record SurfScoreResult(
    int? Score, string? Label, double? SizePoints, double? PeriodPoints, double? WindPoints,
    double? Exposure, double? OffshoreAlignment, IReadOnlyList<string> Missing);

/// <summary>
/// A rough, openly documented estimate of how good the surf might be, from forecast swell and wind and which way
/// the beach faces. It is not a surf report and not from any third party. See docs/surf-score.md for the reasoning.
/// A missing input gives no score at all, never a guess.
/// </summary>
public static class SurfScore
{
    public const int MaxSize = 40;
    public const int MaxPeriod = 30;
    public const int MaxWind = 30;

    // Effective swell height (metres) to points: too small to ride, a sweet spot, then too big.
    public static readonly (double X, double Y)[] SizeCurve =
        { (0, 0), (0.3, 5), (0.6, 15), (1.0, 28), (1.5, 38), (2.5, 40), (3.5, 30), (5, 12), (7, 0) };

    // Swell period (seconds) to points: longer periods carry more power and shape better waves.
    public static readonly (double X, double Y)[] PeriodCurve =
        { (6, 0), (8, 10), (10, 20), (12, 26), (14, 30), (18, 30) };

    public static SurfScoreResult Calculate(SurfInputs i)
    {
        var missing = new List<string>();
        if (i.SwellHeightM is null) missing.Add("swell height");
        if (i.SwellPeriodS is null) missing.Add("swell period");
        if (i.SwellFromDeg is null) missing.Add("swell direction");
        if (i.WindSpeedKmh is null) missing.Add("wind speed");
        if (i.WindFromDeg is null) missing.Add("wind direction");
        if (i.FacingDeg is null) missing.Add("which way the beach faces");
        if (missing.Count > 0) return new SurfScoreResult(null, null, null, null, null, null, null, missing);

        // a swell from straight out to sea fully reaches the beach, one from the side or behind the headland does not
        var exposure = Math.Max(0, Math.Cos(ToRadians(AngleDifference(i.SwellFromDeg!.Value, i.FacingDeg!.Value))));
        var size = Interpolate(SizeCurve, i.SwellHeightM!.Value * exposure);

        // period and wind only count as far as there is something to ride: a tiny swell with a long period, or
        // perfect wind on a flat sea, is still not good surf
        var rideable = Math.Min(1, size / 15);
        var period = Interpolate(PeriodCurve, i.SwellPeriodS!.Value) * Math.Min(1, 2 * exposure) * rideable;

        // offshore wind blows from the land toward the sea, so it comes from the direction opposite the one the beach faces
        var alignment = Math.Cos(ToRadians(AngleDifference(i.WindFromDeg!.Value, i.FacingDeg.Value + 180)));
        var speed = i.WindSpeedKmh!.Value;
        var quality = speed <= 8
            ? 1.0
            : Math.Clamp(0.5 + 0.5 * alignment * Math.Min(speed, 30) / 30 - 0.5 * Math.Max(0, speed - 30) / 30, 0, 1);

        var wind = MaxWind * quality * rideable;

        var total = (int)Math.Round(Math.Clamp(size + period + wind, 0, 100), MidpointRounding.AwayFromZero);
        return new SurfScoreResult(total, Label(total), size, period, wind, exposure, alignment, missing);
    }

    public static string Label(int score) => score switch
    {
        < 25 => "Poor",
        < 50 => "Fair",
        < 75 => "Good",
        _ => "Great",
    };

    /// <summary>Smallest angle between two bearings, 0 to 180, so 350 and 10 are 20 apart.</summary>
    public static double AngleDifference(double a, double b)
    {
        var d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180;

    /// <summary>Straight lines between the points, flat beyond either end.</summary>
    public static double Interpolate((double X, double Y)[] points, double x)
    {
        if (x <= points[0].X) return points[0].Y;
        for (var i = 1; i < points.Length; i++)
        {
            if (x > points[i].X) continue;
            var (x0, y0) = points[i - 1];
            var (x1, y1) = points[i];
            return y0 + (y1 - y0) * (x - x0) / (x1 - x0);
        }
        return points[^1].Y;
    }
}
