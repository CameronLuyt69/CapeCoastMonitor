namespace CapeCoast.Core.Domain;

/// <summary>
/// Range checks on incoming values. A value outside its physical range is dropped to null and
/// flagged, and a value the source didn't send is flagged as missing. Nothing is interpolated.
/// </summary>
public static class ObservationValidator
{
    public static MarineObservation Validate(MarineObservation o)
    {
        var flags = new List<string>();
        var m = o with
        {
            WaveHeightM = Check(o.WaveHeightM, 0, 30, "wave_height_m", flags),
            SwellHeightM = Check(o.SwellHeightM, 0, 30, "swell_height_m", flags),
            SwellPeriodS = Check(o.SwellPeriodS, 0, 40, "swell_period_s", flags),
            SwellDirectionDeg = Check(o.SwellDirectionDeg, 0, 360, "swell_direction_deg", flags),
            WindWaveHeightM = Check(o.WindWaveHeightM, 0, 30, "wind_wave_height_m", flags),
            SeaTempC = Check(o.SeaTempC, -2, 40, "sea_temp_c", flags),
            SeaLevelM = Check(o.SeaLevelM, -10, 10, "sea_level_m", flags),
        };
        return m with { QualityFlags = flags.ToArray() };
    }

    public static WindObservation Validate(WindObservation o)
    {
        var flags = new List<string>();
        var w = o with
        {
            SpeedKmh = Check(o.SpeedKmh, 0, 300, "speed_kmh", flags),
            GustKmh = Check(o.GustKmh, 0, 400, "gust_kmh", flags),
            DirectionDeg = Check(o.DirectionDeg, 0, 360, "direction_deg", flags),
        };
        return w with { QualityFlags = flags.ToArray() };
    }

    public static WeatherObservation Validate(WeatherObservation o)
    {
        var flags = new List<string>();
        var w = o with
        {
            AirTempC = Check(o.AirTempC, -20, 55, "air_temp_c", flags),
            PrecipitationMm = Check(o.PrecipitationMm, 0, 300, "precipitation_mm", flags),
            UvIndex = Check(o.UvIndex, 0, 20, "uv_index", flags),
            VisibilityM = Check(o.VisibilityM, 0, 200000, "visibility_m", flags),
            CloudCoverPct = Check(o.CloudCoverPct, 0, 100, "cloud_cover_pct", flags),
            HumidityPct = Check(o.HumidityPct, 0, 100, "humidity_pct", flags),
        };
        return w with { QualityFlags = flags.ToArray() };
    }

    private static double? Check(double? value, double min, double max, string field, List<string> flags)
    {
        if (value is null)
        {
            flags.Add($"missing:{field}");
            return null;
        }
        if (double.IsNaN(value.Value) || value < min || value > max)
        {
            flags.Add($"out_of_range:{field}");
            return null;
        }
        return value;
    }
}
