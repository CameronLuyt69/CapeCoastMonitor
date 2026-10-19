namespace CapeCoast.Tests;

/// <summary>Guards the bearings the migrations seed, so a careless edit to them can't slip through.</summary>
[Collection("db")]
public class BeachBearingTests
{
    private readonly DatabaseFixture _fx;

    public BeachBearingTests(DatabaseFixture fx) => _fx = fx;

    [Fact]
    public void Every_beach_with_a_bearing_has_a_real_compass_bearing_and_says_where_it_came_from()
    {
        foreach (var (slug, (deg, source)) in _fx.InitialFacing)
        {
            if (deg is null)
            {
                Assert.Equal("unset", source);
                continue;
            }
            Assert.InRange(deg.Value, 0, 359.99);
            Assert.Contains(source, new[] { "coast-default", "osm-coastline", "manual" });
            Assert.True(source != "unset", $"{slug} has a bearing but is marked unset");
        }
    }

    [Theory]
    [InlineData("noordhoek", 270)]     // the long open west-facing beach
    [InlineData("muizenberg", 150)]    // across False Bay to the south-east
    [InlineData("strand", 230)]        // the beachfront faces south-west
    [InlineData("camps-bay", 300)]
    [InlineData("llandudno", 290)]
    [InlineData("fish-hoek", 100)]     // Fish Hoek Bay faces east
    public void The_bearings_estimated_from_the_coastline_are_the_ones_that_were_checked_against_the_geography(string slug, double expected)
    {
        var (deg, source) = _fx.InitialFacing[slug];

        Assert.Equal(expected, deg);
        Assert.Equal("osm-coastline", source);
    }

    [Fact]
    public void Hout_Bay_has_no_bearing_because_the_sampling_point_is_on_the_harbour_wharf()
    {
        var (deg, source) = _fx.InitialFacing["hout-bay"];

        Assert.Null(deg);
        Assert.Equal("unset", source);
    }

    [Fact]
    public void Bearings_are_rounded_to_five_degrees_so_they_do_not_claim_false_precision()
    {
        foreach (var (slug, (deg, source)) in _fx.InitialFacing.Where(f => f.Value.Source == "osm-coastline"))
            Assert.True(deg!.Value % 5 == 0, $"{slug} is {deg}");
    }
}
