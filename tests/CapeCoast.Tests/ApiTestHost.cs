using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CapeCoast.Api;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace CapeCoast.Tests;

/// <summary>Runs the real API in-process on a random port, so tests make genuine HTTP calls.</summary>
public sealed class ApiTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly HttpClientHandler _handler = new() { UseCookies = true };

    public HttpClient Client { get; }
    public IServiceProvider Services => _app.Services;

    private ApiTestHost(WebApplication app, string baseAddress)
    {
        _app = app;
        Client = new HttpClient(_handler) { BaseAddress = new Uri(baseAddress) };
    }

    public static async Task<ApiTestHost> StartAsync(string connectionString, params string[] extraArgs)
    {
        var args = new[]
        {
            "--urls=http://127.0.0.1:0",
            $"--ConnectionStrings:Default={connectionString}",
            "--Auth:AdminUsername=admin", "--Auth:AdminPassword=admin-pass",
            "--Auth:ViewerUsername=viewer", "--Auth:ViewerPassword=viewer-pass",
            "--RateLimits:LoginPerMinute=1000", "--RateLimits:PublicPerMinute=100000", "--RateLimits:AdminPerMinute=100000",
            "--Logging:LogLevel:Default=Warning", "--Telemetry:Enabled=false",
        }.Concat(extraArgs).ToArray();

        var app = ApiApp.Build(args);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new ApiTestHost(app, address);
    }

    /// <summary>A second client with its own cookie jar, e.g. to sign in as someone else.</summary>
    public HttpClient NewClient() => new(new HttpClientHandler { UseCookies = true }) { BaseAddress = Client.BaseAddress };

    public static async Task<HttpClient> SignedIn(ApiTestHost host, string user, string password)
    {
        var client = host.NewClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = user, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    public static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
