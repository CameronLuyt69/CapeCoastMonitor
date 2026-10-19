using System.Globalization;
using System.Text.Json;
using CapeCoast.Core.Domain;

namespace CapeCoast.Infrastructure.Sources;

/// <summary>Shared bits of the two Open-Meteo APIs: query building and response walking.</summary>
internal static class OpenMeteoRequest
{
    public static string Build(SourceOptions o, IReadOnlyList<Location> locations, string hourly)
    {
        var lat = string.Join(",", locations.Select(l => l.Latitude.ToString("0.####", CultureInfo.InvariantCulture)));
        var lon = string.Join(",", locations.Select(l => l.Longitude.ToString("0.####", CultureInfo.InvariantCulture)));
        return $"{o.BaseUrl}?latitude={lat}&longitude={lon}&hourly={hourly}" +
               $"&past_days={o.PastDays}&forecast_days={o.ForecastDays}&timezone=UTC";
    }

    // One location comes back as an object, several as an array in request order.
    public static List<(Location, JsonElement)> HourlyBlocks(string json, IReadOnlyList<Location> locations)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement.Clone();
        var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : new List<JsonElement> { root };

        if (items.Count != locations.Count)
            throw new InvalidDataException($"Asked for {locations.Count} locations but the response held {items.Count}");

        return items.Select((item, i) => (locations[i], item.GetProperty("hourly"))).ToList();
    }

    public static DateTime ParseTime(string s) =>
        DateTime.SpecifyKind(DateTime.ParseExact(s, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture), DateTimeKind.Utc);

    public static double? At(JsonElement hourly, string field, int index)
    {
        if (!hourly.TryGetProperty(field, out var arr) || index >= arr.GetArrayLength()) return null;
        var v = arr[index];
        return v.ValueKind == JsonValueKind.Null ? null : v.GetDouble();
    }
}
