using CapeCoast.Infrastructure.Migrations;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is not set");

builder.Services.AddSerilog((services, cfg) =>
{
    cfg.ReadFrom.Configuration(builder.Configuration)
       .Enrich.FromLogContext()
       .WriteTo.Console();
    var seq = builder.Configuration["Seq:Url"];
    if (!string.IsNullOrEmpty(seq)) cfg.WriteTo.Seq(seq);
});

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

if (args.Contains("--migrate-only"))
{
    return Migrator.Run(connectionString, logger) ? 0 : 1;
}

await host.RunAsync();
return 0;
