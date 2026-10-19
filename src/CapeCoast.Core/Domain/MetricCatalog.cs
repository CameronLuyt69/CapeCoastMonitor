namespace CapeCoast.Core.Domain;

/// <param name="Key">Name used in API calls and alert rules.</param>
/// <param name="Table">Hypertable holding the raw hourly values.</param>
/// <param name="Column">Column in that table.</param>
/// <param name="Unit">Display unit.</param>
/// <param name="DailyView">Continuous aggregate with daily min/max/avg for this metric, if there is one.</param>
/// <param name="DailyPrefix">Column prefix inside that aggregate, e.g. wave gives wave_min, wave_max, wave_avg.</param>
public record MetricDef(string Key, string Table, string Column, string Unit, string? DailyView, string? DailyPrefix);

public static class MetricCatalog
{
    public const string Enterococci = "enterococci";

    public static readonly IReadOnlyList<MetricDef> Series = new[]
    {
        new MetricDef("wave_height", "marine_observations", "wave_height_m", "m", "marine_daily", "wave"),
        new MetricDef("swell_height", "marine_observations", "swell_height_m", "m", "marine_daily", "swell"),
        new MetricDef("swell_period", "marine_observations", "swell_period_s", "s", null, null),
        new MetricDef("swell_direction", "marine_observations", "swell_direction_deg", "deg", null, null),
        new MetricDef("wind_wave_height", "marine_observations", "wind_wave_height_m", "m", null, null),
        new MetricDef("sea_temp", "marine_observations", "sea_temp_c", "C", "marine_daily", "sea_temp"),
        new MetricDef("sea_level", "marine_observations", "sea_level_m", "m", null, null),
        new MetricDef("wind_speed", "wind_observations", "speed_kmh", "km/h", "wind_daily", "speed"),
        new MetricDef("wind_gust", "wind_observations", "gust_kmh", "km/h", null, null),
        new MetricDef("wind_direction", "wind_observations", "direction_deg", "deg", null, null),
        new MetricDef("air_temp", "weather_observations", "air_temp_c", "C", "weather_daily", "air_temp"),
        new MetricDef("precipitation", "weather_observations", "precipitation_mm", "mm", "weather_daily", "precip"),
        new MetricDef("uv_index", "weather_observations", "uv_index", "", "weather_daily", "uv"),
        new MetricDef("visibility", "weather_observations", "visibility_m", "m", "weather_daily", "vis"),
        new MetricDef("cloud_cover", "weather_observations", "cloud_cover_pct", "%", "weather_daily", "cloud"),
        new MetricDef("humidity", "weather_observations", "humidity_pct", "%", "weather_daily", "humidity"),
    };

    public static MetricDef? Find(string key) => Series.FirstOrDefault(m => m.Key == key);

    /// <summary>Metrics an alert rule may watch: every series plus the water quality count.</summary>
    public static bool IsAlertMetric(string key) => key == Enterococci || Find(key) is not null;
}
