using CapeCoast.Core.Sources;
using CapeCoast.Infrastructure.Ingestion;
using CapeCoast.Infrastructure.Sources;
using CapeCoast.Infrastructure.Sources.WaterQuality;
using Microsoft.Extensions.Options;
using Quartz;

namespace CapeCoast.Worker.Jobs;

[DisallowConcurrentExecution]
public class MarineIngestJob : IJob
{
    private readonly IEnumerable<IMarineSource> _sources;
    private readonly IngestionService _ingestion;

    public MarineIngestJob(IEnumerable<IMarineSource> sources, IngestionService ingestion)
    {
        _sources = sources;
        _ingestion = ingestion;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        foreach (var source in _sources)
            await _ingestion.RunMarineAsync(source, context.CancellationToken);
    }
}

[DisallowConcurrentExecution]
public class WindIngestJob : IJob
{
    private readonly IEnumerable<IWindSource> _sources;
    private readonly IngestionService _ingestion;

    public WindIngestJob(IEnumerable<IWindSource> sources, IngestionService ingestion)
    {
        _sources = sources;
        _ingestion = ingestion;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        foreach (var source in _sources)
            await _ingestion.RunWindAsync(source, context.CancellationToken);
    }
}

[DisallowConcurrentExecution]
public class WaterQualityIngestJob : IJob
{
    private readonly IEnumerable<IWaterQualitySource> _sources;
    private readonly WaterQualityIngestionService _ingestion;

    public WaterQualityIngestJob(IEnumerable<IWaterQualitySource> sources, WaterQualityIngestionService ingestion)
    {
        _sources = sources;
        _ingestion = ingestion;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        foreach (var source in _sources)
            await _ingestion.RunAsync(source, context.CancellationToken);
    }
}

[DisallowConcurrentExecution]
public class FreshnessJob : IJob
{
    private readonly FreshnessChecker _checker;
    private readonly SourcesOptions _options;
    private readonly ILogger<FreshnessJob> _log;

    public FreshnessJob(FreshnessChecker checker, IOptions<SourcesOptions> options, ILogger<FreshnessJob> log)
    {
        _checker = checker;
        _options = options.Value;
        _log = log;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var checks = new[]
        {
            (OpenMeteoMarineSource.SourceName, _options.OpenMeteoMarine),
            (OpenMeteoWindSource.SourceName, _options.OpenMeteoWind),
            (CityReportSource.SourceName, (SourceOptions)_options.CityWaterQuality),
        };

        foreach (var (name, o) in checks.Where(c => c.Item2.Enabled))
        {
            var result = await _checker.CheckAsync(name, o.IntervalMinutes, DateTime.UtcNow, context.CancellationToken);
            if (result.Stale)
                _log.LogWarning("Source {Source} is stale: {Reason}", name, result.Reason);
        }
    }
}
