using CapeCoast.Core.Domain;

namespace CapeCoast.Tests;

/// <summary>Each case is worked out by hand from the formula in docs/surf-score.md.</summary>
public class SurfScoreTests
{
    // a beach facing west, so offshore wind blows from the east (90) and a head-on swell comes from the west (270)
    private static SurfInputs West(double hs, double tp, double swellFrom, double windKmh, double windFrom) =>
        new(hs, tp, swellFrom, windKmh, windFrom, FacingDeg: 270);

    [Fact]
    public void A_clean_head_on_swell_with_light_wind_is_great()
    {
        var r = SurfScore.Calculate(West(hs: 1.5, tp: 14, swellFrom: 270, windKmh: 5, windFrom: 90));

        Assert.Equal(38, r.SizePoints!.Value, 6);      // 1.5 m is on the curve at 38
        Assert.Equal(30, r.PeriodPoints!.Value, 6);    // 14 s is the top of the period curve
        Assert.Equal(30, r.WindPoints!.Value, 6);      // 5 km/h is light enough to count as perfect
        Assert.Equal(98, r.Score);
        Assert.Equal("Great", r.Label);
    }

    [Fact]
    public void A_swell_arriving_at_forty_five_degrees_is_partly_blocked_and_the_numbers_follow()
    {
        var r = SurfScore.Calculate(West(hs: 2.0, tp: 10, swellFrom: 315, windKmh: 15, windFrom: 90));

        Assert.Equal(Math.Cos(Math.PI / 4), r.Exposure!.Value, 6);
        // 2.0 m x 0.7071 = 1.414 m effective, between (1.0 m, 28) and (1.5 m, 38): 28 + 0.828 x 10
        Assert.Equal(36.284, r.SizePoints!.Value, 3);
        Assert.Equal(20, r.PeriodPoints!.Value, 6);    // 10 s is 20 points and exposure is above 0.5
        Assert.Equal(1, r.OffshoreAlignment!.Value, 6);
        Assert.Equal(22.5, r.WindPoints!.Value, 6);    // offshore at 15 km/h: 0.5 + 0.5 x 15/30 = 0.75 of 30
        Assert.Equal(79, r.Score);                      // 78.78 rounded
    }

    [Fact]
    public void A_swell_from_the_side_does_not_reach_the_beach_at_all()
    {
        var r = SurfScore.Calculate(West(hs: 3, tp: 16, swellFrom: 0, windKmh: 5, windFrom: 90));   // 90 degrees off

        Assert.Equal(0, r.Exposure!.Value, 6);
        Assert.Equal(0, r.Score);
        Assert.Equal("Poor", r.Label);
    }

    [Fact]
    public void A_swell_from_behind_the_beach_gives_nothing()
    {
        var r = SurfScore.Calculate(West(hs: 3, tp: 16, swellFrom: 90, windKmh: 5, windFrom: 90));

        Assert.Equal(0, r.Exposure!.Value);   // clamped, not negative
        Assert.Equal(0, r.Score);
    }

    [Fact]
    public void A_strong_onshore_wind_removes_all_the_wind_points_but_not_the_swell_ones()
    {
        var r = SurfScore.Calculate(West(hs: 1.5, tp: 14, swellFrom: 270, windKmh: 30, windFrom: 270));

        Assert.Equal(-1, r.OffshoreAlignment!.Value, 6);
        Assert.Equal(0, r.WindPoints!.Value, 6);       // 0.5 - 0.5 x 30/30 = 0
        Assert.Equal(68, r.Score);                      // 38 + 30 + 0
        Assert.Equal("Good", r.Label);
    }

    [Fact]
    public void A_wind_blowing_along_the_beach_is_neither_helpful_nor_harmful()
    {
        // from 0 (north) against a beach facing west: 90 degrees from offshore (east), so alignment is 0 and quality 0.5
        var r = SurfScore.Calculate(West(hs: 1.5, tp: 14, swellFrom: 270, windKmh: 20, windFrom: 0));

        Assert.Equal(0, r.OffshoreAlignment!.Value, 6);
        Assert.Equal(15, r.WindPoints!.Value, 6);
    }

    [Fact]
    public void A_gale_hurts_even_when_it_is_offshore()
    {
        var r = SurfScore.Calculate(West(hs: 1.5, tp: 14, swellFrom: 270, windKmh: 60, windFrom: 90));

        // 0.5 + 0.5 x 1 x 30/30 - 0.5 x 30/30 = 0.5
        Assert.Equal(15, r.WindPoints!.Value, 6);
    }

    [Fact]
    public void Onshore_wind_at_fifteen_kmh_is_a_quarter_quality()
    {
        var r = SurfScore.Calculate(West(hs: 1.5, tp: 14, swellFrom: 270, windKmh: 15, windFrom: 270));

        Assert.Equal(7.5, r.WindPoints!.Value, 6);     // 0.5 - 0.5 x 15/30 = 0.25 of 30
    }

    [Fact]
    public void A_tiny_swell_does_not_earn_period_or_wind_points_so_a_flat_day_is_poor()
    {
        var r = SurfScore.Calculate(West(hs: 0.2, tp: 14, swellFrom: 270, windKmh: 5, windFrom: 90));

        Assert.Equal(3.333, r.SizePoints!.Value, 3);   // 0.2 m on the way from (0, 0) to (0.3 m, 5)
        // rideable = 3.333 / 15 = 0.222, so period 30 x 0.222 and wind 30 x 0.222
        Assert.Equal(6.667, r.PeriodPoints!.Value, 3);
        Assert.Equal(6.667, r.WindPoints!.Value, 3);
        Assert.Equal(17, r.Score);
        Assert.Equal("Poor", r.Label);
    }

    [Fact]
    public void A_huge_swell_is_too_big_to_score()
    {
        var r = SurfScore.Calculate(West(hs: 8, tp: 16, swellFrom: 270, windKmh: 5, windFrom: 90));

        Assert.Equal(0, r.SizePoints!.Value);
        Assert.Equal(0, r.Score);
    }

    [Fact]
    public void The_score_never_leaves_zero_to_one_hundred()
    {
        foreach (var hs in new[] { 0.0, 0.5, 2.5, 6.0, 20.0 })
            foreach (var tp in new[] { 0.0, 9.0, 20.0, 40.0 })
                foreach (var wind in new[] { 0.0, 12.0, 80.0 })
                    foreach (var from in new[] { 0.0, 135.0, 270.0 })
                    {
                        var r = SurfScore.Calculate(West(hs, tp, from, wind, 90));
                        Assert.InRange(r.Score!.Value, 0, 100);
                    }
    }

    [Theory]
    [InlineData(0, "swell height")]
    [InlineData(1, "swell period")]
    [InlineData(2, "swell direction")]
    [InlineData(3, "wind speed")]
    [InlineData(4, "wind direction")]
    [InlineData(5, "which way the beach faces")]
    public void Any_missing_input_gives_no_score_and_says_which(int which, string expected)
    {
        double?[] v = { 1.5, 14, 270, 5, 90, 270 };
        v[which] = null;

        var r = SurfScore.Calculate(new SurfInputs(v[0], v[1], v[2], v[3], v[4], v[5]));

        Assert.Null(r.Score);
        Assert.Null(r.Label);
        Assert.Null(r.SizePoints);
        Assert.Equal(new[] { expected }, r.Missing);
    }

    [Fact]
    public void Several_missing_inputs_are_all_listed()
    {
        var r = SurfScore.Calculate(new SurfInputs(null, null, 270, null, 90, 270));

        Assert.Null(r.Score);
        Assert.Equal(new[] { "swell height", "swell period", "wind speed" }, r.Missing);
    }

    [Theory]
    [InlineData(350, 10, 20)]
    [InlineData(10, 350, 20)]
    [InlineData(359, 1, 2)]
    [InlineData(0, 180, 180)]
    [InlineData(90, 270, 180)]
    [InlineData(45, 45, 0)]
    [InlineData(720, 0, 0)]
    [InlineData(90, 450, 0)]
    public void Angle_differences_wrap_around_north(double a, double b, double expected)
    {
        Assert.Equal(expected, SurfScore.AngleDifference(a, b), 6);
    }

    [Fact]
    public void A_bearing_just_past_north_is_handled_the_same_as_one_just_before_it()
    {
        var before = SurfScore.Calculate(new SurfInputs(1.5, 14, 355, 5, 175, 5));
        var after = SurfScore.Calculate(new SurfInputs(1.5, 14, 5, 5, 185, 355));

        Assert.Equal(before.Score, after.Score);   // both are the same 10 degrees off in the same sense
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1.25, 33)]
    [InlineData(2.5, 40)]
    [InlineData(10, 0)]
    public void The_size_curve_interpolates_between_points_and_is_flat_past_the_ends(double metres, double points)
    {
        Assert.Equal(points, SurfScore.Interpolate(SurfScore.SizeCurve, metres), 6);
    }

    [Theory]
    [InlineData(0, "Poor")]
    [InlineData(24, "Poor")]
    [InlineData(25, "Fair")]
    [InlineData(49, "Fair")]
    [InlineData(50, "Good")]
    [InlineData(74, "Good")]
    [InlineData(75, "Great")]
    [InlineData(100, "Great")]
    public void Labels_change_at_the_documented_thresholds(int score, string label)
    {
        Assert.Equal(label, SurfScore.Label(score));
    }
}
