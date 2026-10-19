using CapeCoast.Core.Alerts;
using CapeCoast.Core.Sources;
using CapeCoast.Infrastructure.Alerts;
using CapeCoast.Infrastructure.Export;
using CapeCoast.Infrastructure.Data;
using CapeCoast.Infrastructure.Ingestion;
using CapeCoast.Infrastructure.Presentation;
using CapeCoast.Infrastructure.Sources;
using CapeCoast.Infrastructure.Sources.WaterQuality;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CapeCoast.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCapeCoastData(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default is not set");

        services.AddSingleton(NpgsqlDataSource.Create(connectionString));
        services.AddSingleton<LocationRepository>();
        services.AddSingleton<ObservationRepository>();
        services.AddSingleton<IngestionRunRepository>();
        services.AddSingleton<WaterQualityRepository>();
        services.AddSingleton<ReadRepository>();
        services.AddSingleton<AdminRepository>();
        services.AddSingleton<IngestionService>();
        services.AddSingleton<WaterQualityIngestionService>();
        services.AddSingleton<FreshnessChecker>();
        services.AddSingleton<StatusService>();
        return services;
    }

    public static IServiceCollection AddOpenMeteoSources(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SourcesOptions>(config.GetSection(SourcesOptions.Section));
        var options = config.GetSection(SourcesOptions.Section).Get<SourcesOptions>() ?? new SourcesOptions();

        services.AddHttpClient(OpenMeteoMarineSource.SourceName, c => c.DefaultRequestHeaders.UserAgent.ParseAdd("CapeCoastMonitor/0.1"));
        services.AddHttpClient(OpenMeteoWindSource.SourceName, c => c.DefaultRequestHeaders.UserAgent.ParseAdd("CapeCoastMonitor/0.1"));

        // a disabled source simply isn't registered, so its job has nothing to run
        if (options.OpenMeteoMarine.Enabled) services.AddSingleton<IMarineSource, OpenMeteoMarineSource>();
        if (options.OpenMeteoWind.Enabled) services.AddSingleton<IWindSource, OpenMeteoWindSource>();

        services.AddHttpClient(CityReportSource.SourceName, c => c.DefaultRequestHeaders.UserAgent.ParseAdd("CapeCoastMonitor/0.1 (non-commercial portfolio project)"));
        if (options.CityWaterQuality.Enabled) services.AddSingleton<IWaterQualitySource, CityReportSource>();
        return services;
    }

    public static IServiceCollection AddCapeCoastAlerts(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AlertOptions>(config.GetSection(AlertOptions.Section));
        services.AddSingleton(TimeProvider.System);
        services.AddHttpClient(N8nWebhookNotifier.ClientName);
        services.AddSingleton<AlertRepository>();
        services.AddSingleton<IAlertNotifier, N8nWebhookNotifier>();
        services.AddSingleton<AlertService>();
        // the ingestion services pick this up and call it after each run that wrote rows
        services.AddSingleton<IPostIngestHook>(sp => sp.GetRequiredService<AlertService>());
        return services;
    }

    public static IServiceCollection AddCapeCoastExport(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<ExportOptions>(config.GetSection(ExportOptions.Section));
        services.AddSingleton<StaticExporter>();
        return services;
    }
}
