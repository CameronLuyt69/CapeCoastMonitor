using CapeCoast.Core.Domain;
using CapeCoast.Infrastructure.Sources;

namespace CapeCoast.Tests;

public class ParserTests
{
    private static readonly Location Muizenberg = new(6, "Muizenberg", "muizenberg", -34.109, 18.471);

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Marine_fixture_parses_every_hour_with_utc_times()
    {
        var rows = OpenMeteoMarineSource.Parse(Fixture("marine-muizenberg.json"), new[] { Muizenberg });

        Assert.Equal(48, rows.Count);
        Assert.All(rows, r => Assert.Equal(DateTimeKind.Utc, r.ObservedAt.Kind));
        Assert.All(rows, r => Assert.Equal(6, r.LocationId));
        Assert.Equal(new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc), rows[0].ObservedAt);
        Assert.Equal(1.82, rows[0].WaveHeightM);
        Assert.Equal(9.15, rows[0].SwellPeriodS);
        Assert.Equal(-0.55, rows[0].SeaLevelM);
    }

    [Fact]
    public void Wind_fixture_parses_speed_gust_and_direction()
    {
        var rows = OpenMeteoWindSource.Parse(Fixture("wind-muizenberg.json"), new[] { Muizenberg });

        Assert.Equal(48, rows.Count);
        Assert.Equal(2.7, rows[0].SpeedKmh);
        Assert.Equal(15.1, rows[0].GustKmh);
        Assert.Equal(247, rows[0].DirectionDeg);
    }

    [Fact]
    public void Null_in_the_response_stays_null()
    {
        const string json = @"{""hourly"":{""time"":[""2026-10-19T00:00""],""wave_height"":[null],""sea_surface_temperature"":[15.5]}}";

        var rows = OpenMeteoMarineSource.Parse(json, new[] { Muizenberg });

        Assert.Null(rows[0].WaveHeightM);
        Assert.Equal(15.5, rows[0].SeaTempC);
        Assert.Null(rows[0].SwellHeightM);
    }

    [Fact]
    public void Array_response_maps_to_locations_in_request_order()
    {
        const string json = @"[
            {""hourly"":{""time"":[""2026-10-19T00:00""],""wave_height"":[1.0]}},
            {""hourly"":{""time"":[""2026-10-19T00:00""],""wave_height"":[2.0]}}]";
        var other = new Location(2, "Camps Bay", "camps-bay", -33.95, 18.37);

        var rows = OpenMeteoMarineSource.Parse(json, new[] { Muizenberg, other });

        Assert.Equal(6, rows[0].LocationId);
        Assert.Equal(1.0, rows[0].WaveHeightM);
        Assert.Equal(2, rows[1].LocationId);
        Assert.Equal(2.0, rows[1].WaveHeightM);
    }

    [Fact]
    public void Location_count_mismatch_is_an_error()
    {
        const string json = @"[{""hourly"":{""time"":[]}}]";
        var other = new Location(2, "Camps Bay", "camps-bay", -33.95, 18.37);

        Assert.Throws<InvalidDataException>(() =>
            OpenMeteoMarineSource.Parse(json, new[] { Muizenberg, other }));
    }
}
