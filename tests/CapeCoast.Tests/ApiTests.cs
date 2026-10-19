using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;

namespace CapeCoast.Tests;

[Collection("db")]
public class ApiTests : IAsyncLifetime
{
    private readonly DatabaseFixture _fx;
    private ApiTestHost _host = null!;
    private long _runId;
    private DateTime _hour;

    public ApiTests(DatabaseFixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        await Seed();
        _host = await ApiTestHost.StartAsync(_fx.ConnectionString);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<int> LocationId(string slug)
    {
        await using var conn = await _fx.Db.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<int>("SELECT id FROM locations WHERE slug = @slug", new { slug });
    }

    /// <summary>72 hourly rows ending now (plus one in the future that must never count as current).</summary>
    private async Task Seed()
    {
        await using var conn = await _fx.Db.OpenConnectionAsync();
        await conn.ExecuteAsync(@"DELETE FROM marine_observations; DELETE FROM wind_observations;
            DELETE FROM water_quality_samples; DELETE FROM source_documents; DELETE FROM alert_events;
            DELETE FROM alert_rules; DELETE FROM maintenance_windows; DELETE FROM ingestion_runs;
            UPDATE locations SET active = true");

        _runId = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO ingestion_runs (source, status, finished_at, rows_fetched, rows_written) VALUES ('open-meteo-marine', 'succeeded', now(), 73, 73) RETURNING id");

        var id = await LocationId("clifton-4th");
        _hour = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day, DateTime.UtcNow.Hour, 0, 0, DateTimeKind.Utc);

        for (var i = -71; i <= 1; i++)
        {
            var t = _hour.AddHours(i);
            await conn.ExecuteAsync(@"
                INSERT INTO marine_observations (location_id, source, observed_at, forecast_issued_at, wave_height_m, swell_height_m, swell_period_s, sea_temp_c, ingestion_run_id)
                VALUES (@id, 'open-meteo-marine', @t, now(), @wave, 1.0, @period, 16.0, @run)",
                new { id, t, wave = 1.0 + (i + 71) * 0.1, period = 8.0 + ((i % 3) + 3) % 3, run = _runId });
            await conn.ExecuteAsync(@"
                INSERT INTO wind_observations (location_id, source, observed_at, forecast_issued_at, speed_kmh, gust_kmh, direction_deg)
                VALUES (@id, 'open-meteo-forecast', @t, now(), 20, 35, 180)", new { id, t });
        }

        var doc = await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO source_documents (source, url, sha256, title)
            VALUES ('cct-coastal-water-quality', 'https://example.test/report.pdf', 'abc123', 'Coastal Water Quality Report') RETURNING id");

        async Task Sample(string slug, string day, int? cfu, string? qualifier, string? rating)
        {
            var loc = await LocationId(slug);
            await conn.ExecuteAsync(@"
                INSERT INTO water_quality_samples (location_id, sampled_at, enterococci_cfu_100ml, qualifier, rating, source_document_id, ingestion_run_id)
                VALUES (@loc, @day::date, @cfu, @qualifier, @rating, @doc, @run)",
                new { loc, day, cfu, qualifier, rating, doc, run = _runId });
        }

        await Sample("clifton-4th", "2026-09-01", 12, null, null);
        await Sample("clifton-4th", "2026-09-08", 300, null, "Amber");
        await Sample("muizenberg", "2026-09-10", 2419, ">", "Red alert");
        await Sample("st-james", "2026-09-10", null, "ND", null);
        await Sample("llandudno", "2026-09-10", 5, null, "Green");
    }

    private Task<HttpResponseMessage> Get(string url) => _host.Client.GetAsync(url);

    private static JsonElement Feature(JsonElement collection, string slug) =>
        collection.GetProperty("features").EnumerateArray().First(f => f.GetProperty("id").GetString() == slug);

    // locations

    [Fact]
    public async Task Locations_is_a_geojson_collection_of_all_beaches()
    {
        var response = await Get("/api/locations");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ApiTestHost.Json(response);

        Assert.Equal("FeatureCollection", json.GetProperty("type").GetString());
        Assert.Equal(10, json.GetProperty("features").GetArrayLength());
        Assert.Equal(240, json.GetProperty("waterQualityThresholdCfu").GetInt32());

        var clifton = Feature(json, "clifton-4th");
        Assert.Equal("Point", clifton.GetProperty("geometry").GetProperty("type").GetString());
        var coordinates = clifton.GetProperty("geometry").GetProperty("coordinates");
        Assert.InRange(coordinates[0].GetDouble(), 18.3, 18.5);    // longitude first
        Assert.InRange(coordinates[1].GetDouble(), -34.1, -33.8);
    }

    [Fact]
    public async Task Latest_marine_value_is_the_current_hour_and_never_a_future_one()
    {
        var json = await ApiTestHost.Json(await Get("/api/locations"));

        var marine = Feature(json, "clifton-4th").GetProperty("properties").GetProperty("marine");
        Assert.Equal(_hour, marine.GetProperty("observedAt").GetDateTime().ToUniversalTime());
        // i = 0 is the 72nd row, so wave = 1.0 + 71 * 0.1
        Assert.Equal(8.1, marine.GetProperty("waveHeightM").GetDouble(), 1);

        var wind = Feature(json, "clifton-4th").GetProperty("properties").GetProperty("wind");
        Assert.Equal(35, wind.GetProperty("gustKmh").GetDouble());
    }

    [Fact]
    public async Task Water_quality_band_follows_the_configured_threshold_and_qualifiers()
    {
        var json = await ApiTestHost.Json(await Get("/api/locations"));

        string Band(string slug) => Feature(json, slug).GetProperty("properties").GetProperty("waterQuality").GetProperty("band").GetString()!;

        Assert.Equal("exceeds", Band("clifton-4th"));   // 300 > 240
        Assert.Equal("exceeds", Band("muizenberg"));    // >2419
        Assert.Equal("unknown", Band("st-james"));      // ND
        Assert.Equal("ok", Band("llandudno"));

        var latest = Feature(json, "clifton-4th").GetProperty("properties").GetProperty("waterQuality");
        Assert.Equal("2026-09-08", latest.GetProperty("sampledAt").GetDateTime().ToString("yyyy-MM-dd"));
        Assert.Equal("https://example.test/report.pdf", latest.GetProperty("documentUrl").GetString());
    }

    [Fact]
    public async Task Beach_without_data_has_null_blocks_rather_than_made_up_values()
    {
        var json = await ApiTestHost.Json(await Get("/api/locations"));

        var props = Feature(json, "noordhoek").GetProperty("properties");
        Assert.Equal(JsonValueKind.Null, props.GetProperty("marine").ValueKind);
        Assert.Equal(JsonValueKind.Null, props.GetProperty("waterQuality").ValueKind);
    }

    // history

    [Fact]
    public async Task Raw_history_returns_points_in_order_with_their_ingestion_run()
    {
        var from = _hour.AddHours(-5).ToString("o");
        var to = _hour.ToString("o");
        var response = await Get($"/api/locations/clifton-4th/history?metric=wave_height&from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ApiTestHost.Json(response);

        Assert.Equal("raw", json.GetProperty("interval").GetString());
        Assert.Equal("m", json.GetProperty("unit").GetString());
        var points = json.GetProperty("points").EnumerateArray().ToList();
        Assert.Equal(6, points.Count);
        Assert.True(points.Zip(points.Skip(1)).All(p => p.First.GetProperty("t").GetDateTime() < p.Second.GetProperty("t").GetDateTime()));
        Assert.Equal(_runId, points[0].GetProperty("ingestionRunId").GetInt64());
    }

    [Fact]
    public async Task Daily_history_carries_min_max_and_avg_from_the_continuous_aggregate()
    {
        var from = _hour.AddDays(-4).ToString("o");
        var response = await Get($"/api/locations/clifton-4th/history?metric=wave_height&interval=day&from={Uri.EscapeDataString(from)}");
        var json = await ApiTestHost.Json(response);

        Assert.Equal("day", json.GetProperty("interval").GetString());
        var points = json.GetProperty("points").EnumerateArray().ToList();
        Assert.NotEmpty(points);
        Assert.All(points, p =>
        {
            var min = p.GetProperty("min").GetDouble();
            var max = p.GetProperty("max").GetDouble();
            var avg = p.GetProperty("avg").GetDouble();
            Assert.True(min <= avg && avg <= max);
        });
        Assert.True(points.Any(p => p.GetProperty("max").GetDouble() > p.GetProperty("min").GetDouble()), "bands should show spread");
    }

    [Fact]
    public async Task Daily_history_works_for_metrics_without_an_aggregate_by_using_raw_rows()
    {
        var from = _hour.AddDays(-4).ToString("o");
        var response = await Get($"/api/locations/clifton-4th/history?metric=swell_period&interval=day&from={Uri.EscapeDataString(from)}");
        var json = await ApiTestHost.Json(response);

        var first = json.GetProperty("points").EnumerateArray().First();
        Assert.InRange(first.GetProperty("min").GetDouble(), 7.9, 10.1);
    }

    [Fact]
    public async Task Weekly_history_is_available()
    {
        var from = _hour.AddDays(-4).ToString("o");
        var response = await Get($"/api/locations/clifton-4th/history?metric=sea_temp&interval=week&from={Uri.EscapeDataString(from)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty((await ApiTestHost.Json(response)).GetProperty("points").EnumerateArray());
    }

    [Theory]
    [InlineData("/api/locations/clifton-4th/history")]                                            // metric missing
    [InlineData("/api/locations/clifton-4th/history?metric=nonsense")]
    [InlineData("/api/locations/clifton-4th/history?metric=wave_height_m;DROP%20TABLE%20locations")]
    [InlineData("/api/locations/clifton-4th/history?metric=wave_height&interval=minute")]
    [InlineData("/api/locations/clifton-4th/history?metric=wave_height&from=yesterday")]
    [InlineData("/api/locations/clifton-4th/history?metric=wave_height&from=2026-10-02T00:00:00Z&to=2026-10-01T00:00:00Z")]
    [InlineData("/api/locations/clifton-4th/history?metric=wave_height&from=2020-01-01T00:00:00Z&to=2026-01-01T00:00:00Z")]
    [InlineData("/api/locations/clifton-4th/history?metric=wave_height&interval=raw&from=2026-01-01T00:00:00Z&to=2026-03-01T00:00:00Z")]
    public async Task Bad_history_requests_are_rejected_with_a_400(string url)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(url)).StatusCode);
    }

    [Fact]
    public async Task Unknown_beach_is_a_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Get("/api/locations/atlantis/history?metric=wave_height")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get("/api/locations/atlantis/water-quality")).StatusCode);
    }

    [Fact]
    public async Task A_data_point_can_be_traced_to_its_ingestion_run()
    {
        var run = await ApiTestHost.Json(await Get($"/api/runs/{_runId}"));

        Assert.Equal("open-meteo-marine", run.GetProperty("source").GetString());
        Assert.Equal("succeeded", run.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await Get("/api/runs/999999")).StatusCode);
    }

    // water quality

    [Fact]
    public async Task Water_quality_lists_newest_first_with_the_source_document()
    {
        var json = await ApiTestHost.Json(await Get("/api/locations/clifton-4th/water-quality"));

        var samples = json.GetProperty("samples").EnumerateArray().ToList();
        Assert.Equal(2, samples.Count);
        Assert.Equal(300, samples[0].GetProperty("enterococciCfu100ml").GetInt32());
        Assert.Equal("Amber", samples[0].GetProperty("rating").GetString());
        Assert.Equal("https://example.test/report.pdf", samples[0].GetProperty("document").GetProperty("url").GetString());
        Assert.Equal(_runId, samples[0].GetProperty("ingestionRunId").GetInt64());
    }

    [Fact]
    public async Task Water_quality_keeps_qualifiers()
    {
        var json = await ApiTestHost.Json(await Get("/api/locations/muizenberg/water-quality"));

        var sample = json.GetProperty("samples")[0];
        Assert.Equal(">", sample.GetProperty("qualifier").GetString());
        Assert.Equal(2419, sample.GetProperty("enterococciCfu100ml").GetInt32());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Water_quality_limit_is_validated(int limit)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Get($"/api/locations/clifton-4th/water-quality?limit={limit}")).StatusCode);
    }

    // health, status, docs

    [Fact]
    public async Task Health_reports_the_database()
    {
        var json = await ApiTestHost.Json(await Get("/api/health"));
        Assert.Equal("ok", json.GetProperty("status").GetString());
        Assert.Equal("up", json.GetProperty("database").GetString());
    }

    [Fact]
    public async Task Status_shows_freshness_per_source()
    {
        var json = await ApiTestHost.Json(await Get("/api/status"));

        var sources = json.GetProperty("sources").EnumerateArray().ToDictionary(s => s.GetProperty("source").GetString()!);
        Assert.Equal(3, sources.Count);
        Assert.False(sources["open-meteo-marine"].GetProperty("stale").GetBoolean());       // run seeded just now
        Assert.True(sources["open-meteo-forecast"].GetProperty("stale").GetBoolean());      // never ran
        Assert.Equal(_runId, sources["open-meteo-marine"].GetProperty("recentRuns")[0].GetProperty("id").GetInt64());
        Assert.NotEqual(JsonValueKind.Null, sources["open-meteo-marine"].GetProperty("latestDataAt").ValueKind);
    }

    [Fact]
    public async Task OpenApi_document_describes_the_endpoints()
    {
        var text = await (await Get("/swagger/v1/swagger.json")).Content.ReadAsStringAsync();

        Assert.Contains("/api/locations", text);
        Assert.Contains("/api/locations/{slug}/history", text);
        Assert.Contains("/api/stream", text);
    }

    // auth and admin

    [Theory]
    [InlineData("/api/admin/alert-rules")]
    [InlineData("/api/admin/maintenance-windows")]
    [InlineData("/api/admin/locations")]
    public async Task Admin_reads_need_a_login(string url)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(url)).StatusCode);
    }

    [Fact]
    public async Task Admin_writes_need_a_login()
    {
        var response = await _host.Client.PostAsJsonAsync("/api/admin/alert-rules", new { name = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("admin", "wrong")]
    [InlineData("nobody", "admin-pass")]
    [InlineData("", "")]
    public async Task Bad_credentials_do_not_sign_in(string user, string password)
    {
        var response = await _host.Client.PostAsJsonAsync("/api/auth/login", new { username = user, password });
        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Viewer_can_read_admin_data_but_not_change_it()
    {
        var viewer = await ApiTestHost.SignedIn(_host, "viewer", "viewer-pass");

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/admin/alert-rules")).StatusCode);

        var post = await viewer.PostAsJsonAsync("/api/admin/alert-rules", ValidRule());
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync("/api/admin/alert-rules/1")).StatusCode);
    }

    private static object ValidRule() => new
    {
        name = "Big swell",
        metric = "swell_height",
        ruleType = "static",
        @operator = ">",
        threshold = 4.5,
        durationMinutes = 60,
        severity = "warning",
        cooldownMinutes = 120,
        enabled = true,
    };

    [Fact]
    public async Task Admin_can_create_update_and_delete_an_alert_rule()
    {
        var admin = await ApiTestHost.SignedIn(_host, "admin", "admin-pass");

        var created = await admin.PostAsJsonAsync("/api/admin/alert-rules", ValidRule());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await ApiTestHost.Json(created)).GetProperty("id").GetInt32();

        var update = await admin.PutAsJsonAsync($"/api/admin/alert-rules/{id}", new
        {
            name = "Big swell",
            metric = "swell_height",
            ruleType = "baseline",
            @operator = ">",
            threshold = 50,
            severity = "critical",
            enabled = false,
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = await ApiTestHost.Json(update);
        Assert.Equal("baseline", updated.GetProperty("ruleType").GetString());
        Assert.False(updated.GetProperty("enabled").GetBoolean());

        var list = await ApiTestHost.Json(await admin.GetAsync("/api/admin/alert-rules"));
        Assert.Equal(1, list.GetArrayLength());

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/alert-rules/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/admin/alert-rules/{id}")).StatusCode);
    }

    [Theory]
    [InlineData("""{"name":"","metric":"swell_height","operator":">","threshold":1}""", "name")]
    [InlineData("""{"name":"x","metric":"made_up","operator":">","threshold":1}""", "metric")]
    [InlineData("""{"name":"x","metric":"swell_height","operator":"=","threshold":1}""", "operator")]
    [InlineData("""{"name":"x","metric":"swell_height","operator":">"}""", "threshold")]
    [InlineData("""{"name":"x","metric":"swell_height","operator":">","threshold":1,"severity":"apocalyptic"}""", "severity")]
    [InlineData("""{"name":"x","metric":"swell_height","operator":">","threshold":1,"ruleType":"magic"}""", "ruleType")]
    [InlineData("""{"name":"x","metric":"swell_height","operator":">","threshold":-5,"ruleType":"baseline"}""", "threshold")]
    [InlineData("""{"name":"x","metric":"swell_height","operator":">","threshold":1,"cooldownMinutes":-1}""", "cooldownMinutes")]
    [InlineData("""{"name":"x","metric":"swell_height","operator":">","threshold":1,"locationId":99999}""", "locationId")]
    public async Task Invalid_alert_rules_are_rejected_naming_the_field(string body, string field)
    {
        var admin = await ApiTestHost.SignedIn(_host, "admin", "admin-pass");

        var response = await admin.PostAsync("/api/admin/alert-rules", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await ApiTestHost.Json(response)).GetProperty("errors").TryGetProperty(field, out _), $"expected an error for {field}");
    }

    [Fact]
    public async Task Water_quality_alert_rules_are_accepted_so_thresholds_stay_configurable()
    {
        var admin = await ApiTestHost.SignedIn(_host, "admin", "admin-pass");

        var response = await admin.PostAsJsonAsync("/api/admin/alert-rules", new
        {
            name = "Single sample guideline",
            metric = "enterococci",
            @operator = ">",
            threshold = 240,
            severity = "critical",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Maintenance_windows_are_validated_and_stored()
    {
        var admin = await ApiTestHost.SignedIn(_host, "admin", "admin-pass");
        var start = DateTimeOffset.UtcNow;

        var backwards = await admin.PostAsJsonAsync("/api/admin/maintenance-windows", new { startsAt = start, endsAt = start.AddHours(-1) });
        Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);

        var ok = await admin.PostAsJsonAsync("/api/admin/maintenance-windows", new { startsAt = start, endsAt = start.AddHours(2), reason = "Sensor swap" });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var id = (await ApiTestHost.Json(ok)).GetProperty("id").GetInt32();

        Assert.Equal(1, (await ApiTestHost.Json(await admin.GetAsync("/api/admin/maintenance-windows"))).GetArrayLength());
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/maintenance-windows/{id}")).StatusCode);
    }

    [Fact]
    public async Task Admin_can_add_and_edit_a_location_and_duplicates_conflict()
    {
        var admin = await ApiTestHost.SignedIn(_host, "admin", "admin-pass");
        var body = new { name = "Test Cove", slug = "test-cove", coast = "Atlantic", lon = 18.3, lat = -34.0, citySiteCode = "TS01" };

        var created = await admin.PostAsJsonAsync("/api/admin/locations", body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await ApiTestHost.Json(created)).GetProperty("id").GetInt32();

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/admin/locations", body)).StatusCode);

        var edited = await admin.PutAsJsonAsync($"/api/admin/locations/{id}", new { name = "Test Cove", coast = "False Bay", lon = 18.3, lat = -34.0, active = false });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.False((await ApiTestHost.Json(edited)).GetProperty("active").GetBoolean());

        // an inactive beach drops out of the public map
        var json = await ApiTestHost.Json(await Get("/api/locations"));
        Assert.DoesNotContain(json.GetProperty("features").EnumerateArray(), f => f.GetProperty("id").GetString() == "test-cove");

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/locations",
            new { name = "Bad", slug = "Bad Slug!", coast = "Pacific", lon = 500, lat = 95 })).StatusCode);

        await using var conn = await _fx.Db.OpenConnectionAsync();
        await conn.ExecuteAsync("DELETE FROM locations WHERE slug = 'test-cove'");
    }

    [Fact]
    public async Task Logging_out_ends_the_session()
    {
        var admin = await ApiTestHost.SignedIn(_host, "admin", "admin-pass");
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/auth/me")).StatusCode);

        await admin.PostAsync("/api/auth/logout", null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync("/api/admin/alert-rules")).StatusCode);
    }

    [Fact]
    public async Task The_session_cookie_is_httponly_and_samesite_strict()
    {
        var response = await _host.Client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin-pass" });

        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("ccm.auth"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    // server-sent events

    [Fact]
    public async Task Stream_pushes_an_update_when_a_run_writes_new_rows()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/stream");
        using var response = await _host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var lines = new List<string>();
        var runs = new CapeCoast.Infrastructure.Data.IngestionRunRepository(_fx.Db);

        var sender = Task.Run(async () =>
        {
            // the LISTEN connection is made at start-up, but give it a few tries in case it is still arriving
            for (var i = 0; i < 10 && !timeout.IsCancellationRequested; i++)
            {
                await Task.Delay(300);
                var id = await runs.StartAsync("open-meteo-marine", "sse-test");
                await runs.FinishAsync(id, "succeeded", 10, 10, null);
            }
        });

        while (!timeout.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(timeout.Token);
            if (line is null) break;
            lines.Add(line);
            if (line.StartsWith("data:") && lines.Contains("event: update")) break;
        }

        Assert.Contains("event: hello", lines);
        Assert.Contains("event: update", lines);
        var data = lines.Last(l => l.StartsWith("data:"));
        Assert.Contains("open-meteo-marine", data);
        timeout.Cancel();
        await sender.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { });
    }

    [Fact]
    public async Task A_run_that_wrote_nothing_does_not_notify_anyone()
    {
        var notifier = _host.Services.GetRequiredService<CapeCoast.Api.IngestionNotifier>();
        using var subscription = notifier.Subscribe();
        await Task.Delay(500);   // let the listener settle

        var runs = new CapeCoast.Infrastructure.Data.IngestionRunRepository(_fx.Db);
        var id = await runs.StartAsync("open-meteo-marine", "quiet");
        await runs.FinishAsync(id, "succeeded", 10, 0, null);
        await Task.Delay(500);

        Assert.False(subscription.Reader.TryRead(out _));
    }
}

[Collection("db")]
public class ApiRateLimitTests
{
    private readonly DatabaseFixture _fx;

    public ApiRateLimitTests(DatabaseFixture fx) => _fx = fx;

    [Fact]
    public async Task Public_endpoints_return_429_once_the_limit_is_used_up()
    {
        await using var host = await ApiTestHost.StartAsync(_fx.ConnectionString, "--RateLimits:PublicPerMinute=3");

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) codes.Add((await host.Client.GetAsync("/api/health")).StatusCode);

        Assert.Equal(3, codes.Count(c => c == HttpStatusCode.OK));
        Assert.Equal(2, codes.Count(c => c == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task Login_attempts_are_limited_so_passwords_cannot_be_brute_forced()
    {
        await using var host = await ApiTestHost.StartAsync(_fx.ConnectionString, "--RateLimits:LoginPerMinute=3");

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
            codes.Add((await host.Client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "guess" + i })).StatusCode);

        Assert.Equal(3, codes.Count(c => c == HttpStatusCode.Unauthorized));
        Assert.Equal(2, codes.Count(c => c == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task One_client_can_only_hold_a_few_streams_open()
    {
        await using var host = await ApiTestHost.StartAsync(_fx.ConnectionString, "--RateLimits:StreamsPerClient=2");

        var open = new List<HttpResponseMessage>();
        try
        {
            for (var i = 0; i < 3; i++)
                open.Add(await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/stream"), HttpCompletionOption.ResponseHeadersRead));

            Assert.Equal(2, open.Count(r => r.StatusCode == HttpStatusCode.OK));
            Assert.Equal(1, open.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
        }
        finally
        {
            foreach (var r in open) r.Dispose();
        }
    }

    [Fact]
    public async Task The_placeholder_password_from_env_example_never_works()
    {
        await using var host = await ApiTestHost.StartAsync(_fx.ConnectionString, "--Auth:AdminPassword=change-me");

        var response = await host.Client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "change-me" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logins_for_a_role_without_a_configured_password_are_refused()
    {
        await using var host = await ApiTestHost.StartAsync(_fx.ConnectionString, "--Auth:AdminPassword=", "--Auth:ViewerPassword=");

        var response = await host.Client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "anything" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
