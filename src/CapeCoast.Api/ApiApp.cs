using Microsoft.AspNetCore.DataProtection;
using System.Threading.RateLimiting;
using CapeCoast.Api.Endpoints;
using CapeCoast.Infrastructure;
using CapeCoast.Infrastructure.Sources;
using CapeCoast.Infrastructure.Telemetry;
using Microsoft.AspNetCore.Authentication.Cookies;
using Serilog;
using Serilog.Events;

namespace CapeCoast.Api;

public static class ApiApp
{
    // Leaflet sets inline styles from script, hence style-src 'unsafe-inline'. Scripts stay same-origin only.
    private const string Csp = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
                               "img-src 'self' data: https://tile.openstreetmap.org; connect-src 'self'; " +
                               "frame-ancestors 'none'; base-uri 'none'; form-action 'self'";

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Host.UseSerilog((context, services, cfg) =>
        {
            cfg.ReadFrom.Configuration(context.Configuration)
               .Enrich.FromLogContext()
               .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {Message:lj}{NewLine}{Exception}");
            var seq = context.Configuration["Seq:Url"];
            if (!string.IsNullOrEmpty(seq)) cfg.WriteTo.Seq(seq);
        });

        builder.Services.AddCapeCoastData(builder.Configuration);
        builder.Services.Configure<SourcesOptions>(builder.Configuration.GetSection(SourcesOptions.Section));
        builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.Section));
        builder.Services.Configure<RateLimitOptions>(builder.Configuration.GetSection(RateLimitOptions.Section));
        builder.Services.Configure<WaterQualityDisplayOptions>(builder.Configuration.GetSection(WaterQualityDisplayOptions.Section));

        builder.Services.AddCapeCoastTelemetry(builder.Configuration, "api", instrumentIncomingRequests: true);

        builder.Services.AddSingleton<IngestionNotifier>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<IngestionNotifier>());

        // sign-in cookies are encrypted with these keys, so keep them somewhere that survives a restart
        var keysPath = builder.Configuration["DataProtection:KeysPath"];
        if (!string.IsNullOrWhiteSpace(keysPath))
            builder.Services.AddDataProtection().SetApplicationName("CapeCoastMonitor").PersistKeysToFileSystem(new DirectoryInfo(keysPath));

        AddAuth(builder.Services);
        AddRateLimiting(builder.Services, builder.Configuration);

        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(o =>
        {
            o.SwaggerDoc("v1", new()
            {
                Title = "Cape Coast Monitor API",
                Version = "v1",
                Description = "Ocean conditions, wind and coastal water quality for Cape Town beaches. Public and read-only, except /api/admin."
            });
        });

        var app = builder.Build();

        app.UseCorrelationId();
        app.UseSerilogRequestLogging(o =>
        {
            // the health check and the long-lived event stream would drown out everything else
            o.GetLevel = (ctx, _, ex) => ex is not null || ctx.Response.StatusCode >= 500 ? LogEventLevel.Error
                : ctx.Request.Path.StartsWithSegments("/api/health") || ctx.Request.Path.StartsWithSegments("/api/stream")
                    ? LogEventLevel.Debug : LogEventLevel.Information;
        });

        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            // OpenStreetMap's tile servers ask browsers to identify the site with a Referer, so send the origin (never the path)
            context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            // Swagger UI ships inline scripts, so the strict policy is for the app itself
            if (!context.Request.Path.StartsWithSegments("/swagger"))
                context.Response.Headers["Content-Security-Policy"] = Csp;
            await next();
        });

        app.UseSwagger();
        app.UseSwaggerUI(o => o.SwaggerEndpoint("/swagger/v1/swagger.json", "Cape Coast Monitor API v1"));

        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapLocationEndpoints();
        app.MapStatusEndpoints();
        app.MapStreamEndpoint();
        app.MapAuthEndpoints();
        app.MapAdminEndpoints();

        return app;
    }

    private static void AddAuth(IServiceCollection services)
    {
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = "ccm.auth";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromHours(8);
                o.SlidingExpiration = false;
                // this is an API, so answer with a status instead of redirecting to a login page
                o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
                o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Roles.AdminPolicy, p => p.RequireRole(Roles.Admin))
            .AddPolicy(Roles.ViewerPolicy, p => p.RequireRole(Roles.Viewer, Roles.Admin));
    }

    private static void AddRateLimiting(IServiceCollection services, IConfiguration config)
    {
        var limits = config.GetSection(RateLimitOptions.Section).Get<RateLimitOptions>() ?? new RateLimitOptions();

        static string Client(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            o.AddPolicy(RateLimits.Public, c => RateLimitPartition.GetFixedWindowLimiter(Client(c),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = limits.PublicPerMinute, Window = TimeSpan.FromMinutes(1) }));

            o.AddPolicy(RateLimits.Login, c => RateLimitPartition.GetFixedWindowLimiter(Client(c),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = limits.LoginPerMinute, Window = TimeSpan.FromMinutes(1) }));

            o.AddPolicy(RateLimits.Admin, c => RateLimitPartition.GetFixedWindowLimiter(Client(c),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = limits.AdminPerMinute, Window = TimeSpan.FromMinutes(1) }));

            // each open stream holds a connection, so cap how many one client can keep
            o.AddPolicy(RateLimits.Stream, c => RateLimitPartition.GetConcurrencyLimiter(Client(c),
                _ => new ConcurrencyLimiterOptions { PermitLimit = limits.StreamsPerClient, QueueLimit = 0 }));
        });
    }
}

public static class Roles
{
    public const string Admin = "Admin";
    public const string Viewer = "Viewer";
    public const string AdminPolicy = "AdminOnly";
    public const string ViewerPolicy = "ViewerOrAdmin";
}

public static class RateLimits
{
    public const string Public = "public";
    public const string Login = "login";
    public const string Admin = "admin";
    public const string Stream = "stream";
}
