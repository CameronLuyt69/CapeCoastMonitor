using System.Text.Json;
using CapeCoast.Core.Domain;
using CapeCoast.Infrastructure.Data;
using CapeCoast.Infrastructure.Presentation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CapeCoast.Infrastructure.Export;

public class ExportOptions
{
    public const string Section = "Export";

    /// <summary>When set, the worker rewrites the export here on a schedule.</summary>
    public string? Directory { get; set; }

    public int IntervalMinutes { get; set; } = 60;

    /// <summary>
    /// The City's results are copyrighted and its terms don't allow redistribution without written permission,
    /// so a public snapshot leaves them out unless this is switched on deliberately.
    /// </summary>
    public bool IncludeWaterQuality { get; set; }

    public int BackDays { get; set; } = 7;
    public int ForwardDays { get; set; } = 3;
    public int WaterQualitySamples { get; set; } = 10;
}

public record ExportResult(string Directory, int Beaches, int Files, bool IncludedWaterQuality);

/// <summary>
/// Writes the data the map needs as plain JSON files, so the same frontend can run from GitHub Pages without
/// the server. The files have exactly the shapes the API returns.
/// </summary>
public class StaticExporter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly ReadRepository _repo;
    private readonly StatusService _status;
    private readonly ExportOptions _options;
    private readonly ILogger<StaticExporter> _log;
    private readonly int _thresholdCfu;

    public StaticExporter(ReadRepository repo, StatusService status, IOptions<ExportOptions> options,
        IConfiguration config, ILogger<StaticExporter> log)
    {
        _repo = repo;
        _status = status;
        _options = options.Value;
        _log = log;
        _thresholdCfu = config.GetValue("WaterQuality:SingleSampleThresholdCfu", 240);
    }

    public async Task<ExportResult> ExportAsync(string directory, CancellationToken ct, bool? includeWaterQuality = null)
    {
        var withWaterQuality = includeWaterQuality ?? _options.IncludeWaterQuality;
        var now = DateTime.UtcNow;
        var from = now.AddDays(-_options.BackDays);
        var to = now.AddDays(_options.ForwardDays);
        var files = 0;

        var staging = Path.Combine(directory, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(staging, "beaches"));

        try
        {
            var rows = await _repo.LatestConditionsAsync(ct);
            Write(staging, "locations.geojson", Shapes.Locations(rows, _thresholdCfu, withWaterQuality, now), ref files);
            Write(staging, "status.json", await _status.BuildAsync(ct), ref files);

            foreach (var row in rows)
            {
                var location = new LocationRef(row.Id, row.Slug, row.Name);
                var runIds = new HashSet<long>();
                var history = new Dictionary<string, object>();

                foreach (var metric in MetricCatalog.Series)
                {
                    var raw = await _repo.RawHistoryAsync(metric, location.Id, from, to, 5000, ct);
                    var daily = await _repo.AggregatedHistoryAsync(metric, location.Id, from, to, "day", 5000, ct);
                    foreach (var p in raw.Where(p => p.IngestionRunId is not null)) runIds.Add(p.IngestionRunId!.Value);

                    history[metric.Key] = new
                    {
                        raw = Shapes.History(location, metric, "raw", from, to, false, raw),
                        day = Shapes.History(location, metric, "day", from, to, false, daily),
                    };
                }

                object? waterQuality = null;
                if (withWaterQuality)
                {
                    var samples = await _repo.WaterQualityAsync(location.Id, _options.WaterQualitySamples, ct);
                    foreach (var s in samples.Where(s => s.IngestionRunId is not null)) runIds.Add(s.IngestionRunId!.Value);
                    waterQuality = Shapes.WaterQuality(location, samples, _thresholdCfu);
                }

                // the "which run wrote this point" lookup the panel does, answered from the snapshot
                var runs = new Dictionary<string, object>();
                foreach (var id in runIds)
                    if (await _repo.RunAsync(id, ct) is { } run) runs[id.ToString()] = Shapes.Run(run);

                Write(staging, $"beaches/{row.Slug}.json", new { slug = row.Slug, exportedAt = now, waterQuality, history, runs }, ref files);
            }

            Publish(staging, directory);
            _log.LogInformation("Exported {Files} files for {Beaches} beaches to {Directory} (water quality {Included})",
                files, rows.Count, directory, withWaterQuality ? "included" : "left out");
            return new ExportResult(directory, rows.Count, files, withWaterQuality);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static void Write(string root, string relative, object value, ref int files)
    {
        File.WriteAllText(Path.Combine(root, relative), JsonSerializer.Serialize(value, Json));
        files++;
    }

    // everything is written to a staging folder first, so an export that fails part way leaves the previous one untouched
    private static void Publish(string staging, string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, "beaches"));
        foreach (var old in Directory.GetFiles(Path.Combine(directory, "beaches"), "*.json")) File.Delete(old);

        foreach (var file in Directory.GetFiles(staging, "*.*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(directory, Path.GetRelativePath(staging, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(file, target, overwrite: true);
        }
    }
}
