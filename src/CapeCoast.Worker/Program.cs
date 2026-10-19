using System.Text.Json;
using CapeCoast.Infrastructure;
using CapeCoast.Infrastructure.Alerts;
using CapeCoast.Infrastructure.Migrations;
using CapeCoast.Infrastructure.Sources;
using CapeCoast.Infrastructure.Telemetry;
using CapeCoast.Worker.Jobs;
using Microsoft.Extensions.Options;
using Quartz;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is not set");

builder.Services.AddSerilog((services, cfg) =>
{
    cfg.ReadFrom.Configuration(builder.Configuration)
       .Enrich.FromLogContext()
       .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {Message:lj}{NewLine}{Exception}");
    var seq = builder.Configuration["Seq:Url"];
    if (!string.IsNullOrEmpty(seq)) cfg.WriteTo.Seq(seq);
});

builder.Services.AddCapeCoastData(builder.Configuration);
builder.Services.AddOpenMeteoSources(builder.Configuration);
builder.Services.AddCapeCoastAlerts(builder.Configuration);
builder.Services.AddCapeCoastTelemetry(builder.Configuration, "worker", instrumentIncomingRequests: false);

var sources = builder.Configuration.GetSection(SourcesOptions.Section).Get<SourcesOptions>() ?? new SourcesOptions();

builder.Services.AddQuartz(q =>
{
    if (sources.OpenMeteoMarine.Enabled)
        Schedule<MarineIngestJob>(q, "marine", sources.OpenMeteoMarine.IntervalMinutes, startDelaySeconds: 5);
    if (sources.OpenMeteoWind.Enabled)
        Schedule<WindIngestJob>(q, "wind", sources.OpenMeteoWind.IntervalMinutes, startDelaySeconds: 15);

    if (sources.CityWaterQuality.Enabled)
        Schedule<WaterQualityIngestJob>(q, "water-quality", sources.CityWaterQuality.IntervalMinutes, startDelaySeconds: 30);

    Schedule<FreshnessJob>(q, "freshness", 15, startDelaySeconds: 60);
});
builder.Services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

if (args.Contains("--migrate-only"))
{
    return Migrator.Run(connectionString, logger) ? 0 : 1;
}

// run the rules once and exit, handy after editing a rule or when checking the webhook setup
if (args.Contains("--evaluate-alerts"))
{
    var summary = await host.Services.GetRequiredService<AlertService>().EvaluateAllAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(summary));
    return 0;
}

await host.RunAsync();
return 0;

static void Schedule<T>(IServiceCollectionQuartzConfigurator q, string name, int everyMinutes, int startDelaySeconds)
    where T : IJob
{
    var key = new JobKey(name);
    q.AddJob<T>(o => o.WithIdentity(key));
    q.AddTrigger(t => t.ForJob(key)
        .WithIdentity($"{name}-trigger")
        .StartAt(DateBuilder.FutureDate(startDelaySeconds, IntervalUnit.Second))
        .WithSimpleSchedule(s => s.WithIntervalInMinutes(everyMinutes).RepeatForever()));
}
