using CapeCoast.Core.Alerts;
using CapeCoast.Infrastructure.Alerts;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CapeCoast.Tests;

public class ManualTime : TimeProvider
{
    private DateTimeOffset _now;
    public ManualTime(DateTime utc) => _now = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
    public DateTime Now => _now.UtcDateTime;
}

public class FakeNotifier : IAlertNotifier
{
    public List<AlertPayload> Sent { get; } = new();
    public bool Fail { get; set; }

    public Task<bool> SendAsync(AlertPayload payload, CancellationToken ct)
    {
        if (Fail) return Task.FromResult(false);
        Sent.Add(payload);
        return Task.FromResult(true);
    }
}

[Collection("db")]
public class AlertServiceTests
{
    private static readonly DateTime Start = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    private readonly DatabaseFixture _fx;
    private readonly ManualTime _time = new(Start);
    private readonly FakeNotifier _notifier = new();

    public AlertServiceTests(DatabaseFixture fx) => _fx = fx;

    private AlertService Service(AlertOptions? options = null) => new(
        new AlertRepository(_fx.Db), _notifier, Options.Create(options ?? new AlertOptions { PublicUrl = "http://test" }),
        _time, NullLogger<AlertService>.Instance);

    private async Task<int> Loc(string slug)
    {
        await using var conn = await _fx.Db.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<int>("SELECT id FROM locations WHERE slug = @slug", new { slug });
    }

    private async Task Reset()
    {
        await using var conn = await _fx.Db.OpenConnectionAsync();
        await conn.ExecuteAsync(@"DELETE FROM alert_events; DELETE FROM alert_rules; DELETE FROM maintenance_windows;
            DELETE FROM marine_observations; DELETE FROM wind_observations; DELETE FROM water_quality_samples;
            UPDATE locations SET active = true");
    }

    private async Task<int> AddRule(string metric = "wave_height", string op = ">", double threshold = 2, int? location = null,
        string type = "static", int duration = 0, int cooldown = 60, string severity = "warning", bool enabled = true)
    {
        await using var conn = await _fx.Db.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<int>(@"
            INSERT INTO alert_rules (name, metric, location_id, rule_type, operator, threshold, duration_minutes, severity, cooldown_minutes, enabled)
            VALUES (@name, @metric, @location, @type, @op, @threshold, @duration, @severity, @cooldown, @enabled) RETURNING id",
            new { name = $"test {metric}", metric, location, type, op, threshold, duration, severity, cooldown, enabled });
    }

    /// <summary>One hourly wave reading per entry, the last one at `at`.</summary>
    private async Task Waves(int location, DateTime at, params double[] values)
    {
        await using var conn = await _fx.Db.OpenConnectionAsync();
        for (var i = 0; i < values.Length; i++)
        {
            await conn.ExecuteAsync(@"
                INSERT INTO marine_observations (location_id, source, observed_at, forecast_issued_at, wave_height_m)
                VALUES (@location, 'test', @t, @at, @v)
                ON CONFLICT (location_id, source, observed_at) DO UPDATE SET wave_height_m = EXCLUDED.wave_height_m",
                new { location, t = at.AddHours(i - values.Length + 1), at, v = values[i] });
        }
    }

    private async Task<(long Id, int Count, bool Resolved, bool OpenedMsg, bool ResolvedMsg, double? Peak)[]> Events()
    {
        await using var conn = await _fx.Db.OpenConnectionAsync();
        var rows = await conn.QueryAsync<(long, int, bool, bool, bool, double?)>(@"
            SELECT id, breach_count, resolved_at IS NOT NULL, last_notified_at IS NOT NULL,
                   resolution_notified_at IS NOT NULL, peak_value FROM alert_events ORDER BY id");
        return rows.ToArray();
    }

    // opening, dedup and resolving

    [Fact]
    public async Task A_breach_opens_one_incident_and_sends_one_message()
    {
        await Reset();
        var loc = await Loc("muizenberg");
        await AddRule(location: loc);
        await Waves(loc, _time.Now, 1.0, 2.5);

        var summary = await Service().EvaluateAllAsync(CancellationToken.None);

        Assert.Equal(1, summary.Opened);
        Assert.Equal(1, summary.Notified);
        var message = Assert.Single(_notifier.Sent);
        Assert.Equal("opened", message.Event);
        Assert.Equal("muizenberg", message.Location.Slug);
        Assert.Equal(2.5, message.Value);
        Assert.Equal("http://test/#beach=muizenberg", message.Link);
        var ev = Assert.Single(await Events());
        Assert.True(ev.OpenedMsg);
    }

    [Fact]
    public async Task A_repeat_breach_updates_the_incident_and_sends_nothing_more()
    {
        await Reset();
        var loc = await Loc("muizenberg");
        await AddRule(location: loc);
        var service = Service();
        await Waves(loc, _time.Now, 2.5);
        await service.EvaluateAllAsync(CancellationToken.None);

        _time.Advance(TimeSpan.FromHours(1));
        await Waves(loc, _time.Now, 3.5);
        var again = await service.EvaluateAllAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(1));
        await Waves(loc, _time.Now, 2.2);
        await service.EvaluateAllAsync(CancellationToken.None);

        Assert.Equal(1, again.Updated);
        Assert.Equal(0, again.Opened);
        Assert.Single(_notifier.Sent);
        var ev = Assert.Single(await Events());
        Assert.Equal(3, ev.Count);
        Assert.Equal(3.5, ev.Peak);
        Assert.False(ev.Resolved);
    }

    [Fact]
    public async Task Clearing_resolves_the_incident_with_a_single_resolved_message()
    {
        await Reset();
        var loc = await Loc("muizenberg");
        await AddRule(location: loc);
        var service = Service();
        await Waves(loc, _time.Now, 2.5);
        await service.EvaluateAllAsync(CancellationToken.None);

        _time.Advance(TimeSpan.FromHours(1));
        await Waves(loc, _time.Now, 1.0);
        var cleared = await service.EvaluateAllAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(1));
        await Waves(loc, _time.Now, 1.1);
        await service.EvaluateAllAsync(CancellationToken.None);

        Assert.Equal(1, cleared.Resolved);
        Assert.Equal(new[] { "opened", "resolved" }, _notifier.Sent.Select(m => m.Event));
        var ev = Assert.Single(await Events());
        Assert.True(ev.Resolved);
        Assert.True(ev.ResolvedMsg);
    }

    [Fact]
    public async Task Evaluating_at_the_same_time_from_two_places_still_gives_one_incident()
    {
        await Reset();
        var loc = await Loc("muizenberg");
        await AddRule(location: loc);
        await Waves(loc, _time.Now, 2.5);
        var a = Service();
        var b = Service();

        await Task.WhenAll(a.EvaluateAllAsync(CancellationToken.None), b.EvaluateAllAsync(CancellationToken.None));

        Assert.Single(await Events());
        Assert.Single(_notifier.Sent);
    }

    [Fact]
    public async Task The_database_itself_refuses_a_second_open_incident_for_the_same_rule_and_beach()
    {
        await Reset();
        var loc = await Loc("muizenberg");
        var rule = await AddRule(location: loc);
        var repo = new AlertRepository(_fx.Db);

        var first = await repo.OpenAsync(rule, loc, 3, _time.Now);
        var second = await repo.OpenAsync(rule, loc, 4, _time.Now);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Single(await Events());
    }

    [Fact]
    public async Task A_rule_for_all_beaches_opens_one_incident_per_beach()
    {
        await Reset();
        await AddRule(location: null);
        await Waves(await Loc("muizenberg"), _time.Now, 3);
        await Waves(await Loc("camps-bay"), _time.Now, 3);
        await Waves(await Loc("llandudno"), _time.Now, 1);

        var summary = await Service().EvaluateAllAsync(CancellationToken.None);

        Assert.Equal(2, summary.Opened);
        Assert.Equal(new[] { "camps-bay", "muizenberg" }, _notifier.Sent.Select(m => m.Location.Slug).OrderBy(s => s));
    }

    [Fact]
    public async Task A_beach_with_no_data_is_left_alone_not_treated_as_fine()
    {
        await Reset();
        await AddRule(location: null);

        var summary = await Service().EvaluateAllAsync(CancellationToken.None);

        Assert.Equal(0, summary.Opened);
        Assert.Equal(10, summary.Skipped);
        Assert.Empty(await Events());
    }

    [Fact]
    public async Task Stale_data_does_not_resolve_an_open_incident()
    {
        await Reset();
        var loc = await Loc("muizenberg");
        await AddRule(location: loc);
        var service = Service();
        await Waves(loc, _time.Now, 2.5);
        await service.EvaluateAllAsync(CancellationToken.None);

        _time.Advance(TimeSpan.FromHours(12));   // ingestion has stalled, the newest reading is now 12 hours old
        var summary = await service.EvaluateAllAsync(CancellationToken.None);

        Assert.Equal(0, summary.Resolved);
        Assert.False((await Events()).Single().Resolved);
        Assert.Single(_notifier.Sent);
    }

    [Fact]
    public async Task Future_forecast_hours_are_never_evaluated_as_current()
    {
        await Reset();
        var loc = await Loc("muizenberg");
        await AddRule(location: loc);
        await Waves(loc, _time.Now.AddHours(5), 9, 9, 9);   // a big swell forecast for later, nothing for now

        var summary = await Service().EvaluateAllAsync(CancellationToken.None);

        Assert.Equal(0, summary.Opened);
    }

    // maintenance windows

    private async Task AddWindow(int? location, TimeSpan from, TimeSpan to)
    {
        await using var conn = await _fx.Db.OpenConnectionAsync();
        await conn.ExecuteAsync("INSERT INTO maintenance_windows (location_id, starts_at, ends_at, reason) VALUES (@location, @s, @e, 'test')",
            new { location, s = _time.Now + from, e = _time.Now + to });
    }

    [Fact]
    public async Task A_maintenance_window_suppresses_new_incidents_and_messages()
    {
        await Reset();
        var loc = await Loc("muizenberg");
        await AddRule(location: loc);
        await Waves(loc, _time.Now, 3);
        await AddWindow(null, TimeSpan.FromMinutes(-10), TimeSpan.FromHours(1));

        var summary = await Service().EvaluateAllAsync(CancellationToken.None);

        Assert.Equal(1, summary.Suppressed);
        Assert.Equal(0, summary.Opened);
        Assert.Empty(_notifier.Sent);
        Assert.Empty(await Events());
    }

    [Fact]
    public async Task Once_the_window_ends_a_continuing_breach_is_reported()
    {
        await Reset();
        var loc = await Loc("muizenberg");
        await AddRule(location: loc);
        var service = Service();
        await Waves(loc, _time.Now, 3);
        await AddWindow(loc, TimeSpan.FromMinutes(-10), TimeSpan.FromHours(1));
        await service.EvaluateAllAsync(CancellationToken.None);

        _time.Advance(TimeSpan.FromHours(2));
        await Waves(loc, _time.Now, 3);
        var after = await service.EvaluateAllAsync(CancellationToken.None);

        Assert.Equal(1, after.Opened);
        Assert.Single(_notifier.Sent);
    }

    [Fact]
    public async Task A_window_for_another_beach_does_not_suppress_this_one()
    {
        await Reset();
        var loc = await Loc("muizenberg");
        await AddRule(location: loc);
        await Waves(loc, _time.Now, 3);
        await AddWindow(await Loc("camps-bay"), TimeSpan.FromMinutes(-10), TimeSpan.FromHours(1));

        var summary = await Service().EvaluateAllAsync(CancellationToken.None);

        Assert.Equal(1, summary.Opened);
        Assert.Equal(0, summary.Suppressed);
    }

    [Fact]
    public async Task A_window_freezes_an_open_incident_instead_of_resolving_it()
    {
        await Reset();
        var loc = await Loc("muizenberg");
        await AddRule(location: loc);
        var service = Service();
        await Waves(loc, _time.Now, 3);
        await service.EvaluateAllAsync(CancellationToken.None);

        await AddWindow(null, TimeSpan.FromMinutes(-10), TimeSpan.FromHours(3));
        _time.Advance(TimeSpan.FromHours(1));
        await Waves(loc, _time.Now, 1);            // it would resolve, but nobody should hear about it mid-maintenance
        await service.EvaluateAllAsync(CancellationToken.None);

        Assert.False((await Events()).Single().Resolved);
        Assert.Single(_notifier.Sent);
    }

    // cooldown

    [Fact]
    public async Task A_flapping_value_does_not_message_again_inside_the_cooldown()
    {
        await Reset();
        var loc = await Loc("muizenberg");
        await AddRule(location: loc, cooldown: 180);
        var service = Service();

        await Waves(loc, _time.Now, 3);
        await service.EvaluateAllAsync(CancellationToken.None);                       // opened
        _time.Advance(TimeSpan.FromHours(1));
        await Waves(loc, _time.Now, 1);
        await service.EvaluateAllAsync(CancellationToken.None);                       // resolved
        _time.Advance(TimeSpan.FromHours(1));
        await Waves(loc, _time.Now, 3);
        var inside = await service.EvaluateAllAsync(CancellationToken.None);          // breaches again, 1 h after the last message

        Assert.Equal(1, inside.Deferred);
        Assert.Equal(0, inside.Opened);
        Assert.Equal(2, _notifier.Sent.Count);
        Assert.Single(await Events());
    }

    [Fact]
    public async Task After_the_cooldown_a_new_breach_opens_a_new_incident()
    {
        await Reset();
        var loc = await Loc("muizenberg");
        await AddRule(location: loc, cooldown: 180);
        var service = Service();

        await Waves(loc, _time.Now, 3);
        await service.EvaluateAllAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(1));
        await Waves(loc, _time.Now, 1);
        await service.EvaluateAllAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(4));
        await Waves(loc, _time.Now, 3);
        var later = await service.EvaluateAllAsync(CancellationToken.None);

        Assert.Equal(1, later.Opened);
        Assert.Equal(2, (await Events()).Length);
        Assert.Equal(3, _notifier.Sent.Count);
    }

    // delivery failures

    [Fact]
    public async Task A_message_that_could_not_be_delivered_is_retried_without_a_duplicate_incident()
    {
        await Reset();
        var loc = await Loc("muizenberg");
        await AddRule(location: loc);
        var service = Service();
        await Waves(loc, _time.Now, 3);

        _notifier.Fail = true;
        var failed = await service.EvaluateAllAsync(CancellationToken.None);
        Assert.Equal(1, failed.NotifyFailed);
        Assert.False((await Events()).Single().OpenedMsg);

        _notifier.Fail = false;
        _time.Advance(TimeSpan.FromMinutes(30));
        var retried = await service.EvaluateAllAsync(CancellationToken.None);

        Assert.Equal(1, retried.Notified);
        Assert.Equal(1, retried.Updated);
        Assert.Single(await Events());
        Assert.Single(_notifier.Sent);
        Assert.True((await Events()).Single().OpenedMsg);
    }

    [Fact]
    public async Task A_resolved_message_that_failed_is_retried_later()
    {
        await Reset();
        var loc = await Loc("muizenberg");
        await AddRule(location: loc);
        var service = Service();
        await Waves(loc, _time.Now, 3);
        await service.EvaluateAllAsync(CancellationToken.None);

        _notifier.Fail = true;
        _time.Advance(TimeSpan.FromHours(1));
        await Waves(loc, _time.Now, 1);
        await service.EvaluateAllAsync(CancellationToken.None);
        var ev = (await Events()).Single();
        Assert.True(ev.Resolved);
        Assert.False(ev.ResolvedMsg);

        _notifier.Fail = false;
        _time.Advance(TimeSpan.FromHours(1));
        await Waves(loc, _time.Now, 1);
        await service.EvaluateAllAsync(CancellationToken.None);

        Assert.True((await Events()).Single().ResolvedMsg);
        Assert.Equal(new[] { "opened", "resolved" }, _notifier.Sent.Select(m => m.Event));
    }

    [Fact]
    public async Task An_incident_that_was_never_announced_is_resolved_silently()
    {
        await Reset();
        var loc = await Loc("muizenberg");
        await AddRule(location: loc);
        var service = Service();
        await Waves(loc, _time.Now, 3);

        _notifier.Fail = true;
        await service.EvaluateAllAsync(CancellationToken.None);       // opened but never delivered
        _notifier.Fail = false;
        _time.Advance(TimeSpan.FromHours(1));
        await Waves(loc, _time.Now, 1);
        await service.EvaluateAllAsync(CancellationToken.None);

        Assert.Empty(_notifier.Sent);                                  // nobody was told it started, so nobody hears it ended
        Assert.True((await Events()).Single().Resolved);
    }

    // rule types

    [Fact]
    public async Task A_duration_rule_waits_until_the_threshold_has_held_long_enough()
    {
        await Reset();
        var loc = await Loc("muizenberg");
        await AddRule(location: loc, duration: 120);
        var service = Service();

        await Waves(loc, _time.Now, 3, 3);               // two readings, one hour of history
        var early = await service.EvaluateAllAsync(CancellationToken.None);
        await Waves(loc, _time.Now, 3, 3, 3);            // now two hours are covered
        var held = await service.EvaluateAllAsync(CancellationToken.None);

        Assert.Equal(0, early.Opened);
        Assert.Equal(1, held.Opened);
    }

    [Fact]
    public async Task A_baseline_rule_compares_with_the_trailing_thirty_days()
    {
        await Reset();
        var loc = await Loc("muizenberg");
        await using (var conn = await _fx.Db.OpenConnectionAsync())
        {
            // 30 days of hourly readings around 1.0 m
            await conn.ExecuteAsync(@"
                INSERT INTO marine_observations (location_id, source, observed_at, forecast_issued_at, wave_height_m)
                SELECT @loc, 'test', t, @now, 1.0
                FROM generate_series(@from::timestamptz, @to::timestamptz, interval '1 hour') AS t",
                new { loc, now = _time.Now, from = _time.Now.AddDays(-30), to = _time.Now.AddHours(-1) });
        }
        await AddRule(location: loc, type: "baseline", op: ">", threshold: 50);
        var service = Service();

        await Waves(loc, _time.Now, 1.3);                 // +30%, inside the norm
        var normal = await service.EvaluateAllAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(1));
        await Waves(loc, _time.Now, 2.0);                 // +100%
        var unusual = await service.EvaluateAllAsync(CancellationToken.None);

        Assert.Equal(0, normal.Opened);
        Assert.Equal(1, unusual.Opened);
        var message = Assert.Single(_notifier.Sent);
        Assert.Equal("baseline", message.Rule.Type);
        Assert.Equal(1.0, message.Baseline!.Mean!.Value, 2);
        Assert.Equal(100, message.Baseline.DeviationPercent!.Value, 0);
    }

    [Fact]
    public async Task A_baseline_rule_does_nothing_until_there_is_history()
    {
        await Reset();
        var loc = await Loc("muizenberg");
        await AddRule(location: loc, type: "baseline", op: ">", threshold: 10);
        await Waves(loc, _time.Now, 1, 1, 5);

        var summary = await Service().EvaluateAllAsync(CancellationToken.None);

        Assert.Equal(0, summary.Opened);
        Assert.Equal(1, summary.Skipped);
    }

    private async Task Sample(int loc, DateTime day, int cfu)
    {
        await using var conn = await _fx.Db.OpenConnectionAsync();
        await conn.ExecuteAsync(@"INSERT INTO water_quality_samples (location_id, sampled_at, enterococci_cfu_100ml) VALUES (@loc, @day, @cfu)",
            new { loc, day, cfu });
    }

    [Fact]
    public async Task The_water_quality_guideline_is_just_a_rule_and_a_fresh_exceedance_alerts()
    {
        await Reset();
        var loc = await Loc("muizenberg");
        await AddRule(metric: "enterococci", threshold: 240, severity: "critical", location: loc);
        await Sample(loc, _time.Now.AddDays(-3), 300);

        var summary = await Service().EvaluateAllAsync(CancellationToken.None);

        Assert.Equal(1, summary.Opened);
        var message = Assert.Single(_notifier.Sent);
        Assert.Equal("cfu/100ml", message.Unit);
        Assert.Equal("critical", message.Severity);
    }

    [Fact]
    public async Task A_different_threshold_changes_the_outcome_so_it_is_not_hard_coded()
    {
        await Reset();
        var loc = await Loc("muizenberg");
        await AddRule(metric: "enterococci", threshold: 200, location: loc);     // the stricter figure some sources quote
        await Sample(loc, _time.Now.AddDays(-3), 220);

        var summary = await Service().EvaluateAllAsync(CancellationToken.None);

        Assert.Equal(1, summary.Opened);
    }

    [Fact]
    public async Task An_old_water_quality_sample_is_not_alerted_on()
    {
        await Reset();
        var loc = await Loc("muizenberg");
        await AddRule(metric: "enterococci", threshold: 240, location: loc);
        await Sample(loc, _time.Now.AddDays(-40), 900);

        var summary = await Service().EvaluateAllAsync(CancellationToken.None);

        Assert.Equal(0, summary.Opened);
        Assert.Equal(1, summary.Skipped);
    }

    // housekeeping

    [Fact]
    public async Task Switching_a_rule_off_closes_its_open_incident_and_says_so()
    {
        await Reset();
        var loc = await Loc("muizenberg");
        var rule = await AddRule(location: loc);
        var service = Service();
        await Waves(loc, _time.Now, 3);
        await service.EvaluateAllAsync(CancellationToken.None);

        await using (var conn = await _fx.Db.OpenConnectionAsync())
            await conn.ExecuteAsync("UPDATE alert_rules SET enabled = false WHERE id = @rule", new { rule });
        await service.EvaluateAllAsync(CancellationToken.None);

        Assert.True((await Events()).Single().Resolved);
        Assert.Equal(new[] { "opened", "resolved" }, _notifier.Sent.Select(m => m.Event));
    }

    [Fact]
    public async Task A_disabled_rule_never_opens_anything()
    {
        await Reset();
        var loc = await Loc("muizenberg");
        await AddRule(location: loc, enabled: false);
        await Waves(loc, _time.Now, 3);

        var summary = await Service().EvaluateAllAsync(CancellationToken.None);

        Assert.Equal(0, summary.Evaluated);
        Assert.Empty(await Events());
    }

    [Fact]
    public async Task The_hook_never_throws_even_if_evaluation_fails()
    {
        var service = new AlertService(new AlertRepository(_fx.Db), _notifier, Options.Create(new AlertOptions()),
            _time, NullLogger<AlertService>.Instance);
        await using var broken = Npgsql.NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=x;Password=x;Timeout=1");
        var failing = new AlertService(new AlertRepository(broken), _notifier, Options.Create(new AlertOptions()),
            _time, NullLogger<AlertService>.Instance);

        await failing.AfterRunAsync("open-meteo-marine", CancellationToken.None);   // no exception means the ingestion run is safe
        await service.AfterRunAsync("open-meteo-marine", CancellationToken.None);
    }
}
