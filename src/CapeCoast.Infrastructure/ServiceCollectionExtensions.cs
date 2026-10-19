using CapeCoast.Core.Sources;
using CapeCoast.Infrastructure.Data;
using CapeCoast.Infrastructure.Ingestion;
using CapeCoast.Infrastructure.Sources;
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
        services.AddSingleton<IngestionService>();
        services.AddSingleton<FreshnessChecker>();
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
        return services;
    }
}
