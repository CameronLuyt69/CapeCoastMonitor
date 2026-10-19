using CapeCoast.Core.Alerts;
using Dapper;
using Npgsql;

namespace CapeCoast.Infrastructure.Alerts;

/// <summary>Where a metric's values live. Built from the metric catalog, never from request input.</summary>
public record MetricSource(string Table, string TimeColumn, string ValueColumn, string Unit);

public class AlertRepository
{
    private readonly NpgsqlDataSource _db;

    public AlertRepository(NpgsqlDataSource db) => _db = db;

    private class RuleRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Metric { get; set; } = "";
        public int? LocationId { get; set; }
        public string RuleType { get; set; } = "";
        public string Operator { get; set; } = "";
        public double Threshold { get; set; }
        public int DurationMinutes { get; set; }
        public string Severity { get; set; } = "";
        public int CooldownMinutes { get; set; }
        public bool Enabled { get; set; }
    }

    public async Task<IReadOnlyList<AlertRule>> EnabledRulesAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<RuleRow>(new CommandDefinition(@"
            SELECT id AS Id, name AS Name, metric AS Metric, location_id AS LocationId, rule_type AS RuleType,
                   operator AS Operator, threshold AS Threshold, duration_minutes AS DurationMinutes,
                   severity AS Severity, cooldown_minutes AS CooldownMinutes, enabled AS Enabled
            FROM alert_rules WHERE enabled ORDER BY id", cancellationToken: ct));
        return rows.Select(r => new AlertRule(r.Id, r.Name, r.Metric, r.LocationId, r.RuleType, r.Operator,
            r.Threshold, r.DurationMinutes, r.Severity, r.CooldownMinutes, r.Enabled)).ToList();
    }

    public async Task<IReadOnlyList<AlertLocation>> LocationsAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<AlertLocation>(new CommandDefinition(
            "SELECT id AS Id, slug AS Slug, name AS Name, coast AS Coast FROM locations WHERE active ORDER BY id",
            cancellationToken: ct))).ToList();
    }

    public async Task<bool> InMaintenanceAsync(int locationId, DateTime now, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(@"
            SELECT EXISTS (SELECT 1 FROM maintenance_windows
                           WHERE starts_at <= @now AND ends_at > @now AND (location_id IS NULL OR location_id = @locationId))",
            new { locationId, now }, cancellationToken: ct));
    }

    // readings

    private class ReadingRow { public DateTime At { get; set; } public double Value { get; set; } }

    /// <summary>Readings from `from` up to `now`, oldest first. Future forecast hours are excluded.</summary>
    public async Task<IReadOnlyList<Reading>> ReadingsAsync(MetricSource m, int locationId, DateTime from, DateTime now,
        CancellationToken ct = default)
    {
        var sql = $@"SELECT {m.TimeColumn} AS At, {m.ValueColumn}::float8 AS Value FROM {m.Table}
                     WHERE location_id = @locationId AND {m.TimeColumn} >= @from AND {m.TimeColumn} <= @now
                       AND {m.ValueColumn} IS NOT NULL
                     ORDER BY {m.TimeColumn}";
        await using var conn = await _db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ReadingRow>(new CommandDefinition(sql, new { locationId, from, now }, cancellationToken: ct));
        return rows.Select(r => new Reading(r.At, r.Value)).ToList();
    }

    public async Task<Reading?> LatestAsync(MetricSource m, int locationId, DateTime notBefore, DateTime now, CancellationToken ct = default)
    {
        var sql = $@"SELECT {m.TimeColumn} AS At, {m.ValueColumn}::float8 AS Value FROM {m.Table}
                     WHERE location_id = @locationId AND {m.TimeColumn} >= @notBefore AND {m.TimeColumn} <= @now
                       AND {m.ValueColumn} IS NOT NULL
                     ORDER BY {m.TimeColumn} DESC LIMIT 1";
        await using var conn = await _db.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<ReadingRow>(new CommandDefinition(sql, new { locationId, notBefore, now }, cancellationToken: ct));
        return row is null ? null : new Reading(row.At, row.Value);
    }

    private class BaselineRow { public double? Mean { get; set; } public long Count { get; set; } }

    /// <summary>Average of the readings before `before`, back as far as `from`.</summary>
    public async Task<Baseline> BaselineAsync(MetricSource m, int locationId, DateTime from, DateTime before, CancellationToken ct = default)
    {
        var sql = $@"SELECT avg({m.ValueColumn}::float8) AS Mean, count({m.ValueColumn}) AS Count FROM {m.Table}
                     WHERE location_id = @locationId AND {m.TimeColumn} >= @from AND {m.TimeColumn} < @before";
        await using var conn = await _db.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleAsync<BaselineRow>(new CommandDefinition(sql, new { locationId, from, before }, cancellationToken: ct));
        return new Baseline(row.Mean, row.Count);
    }

    // incidents

    private const string IncidentColumns = @"id AS Id, rule_id AS RuleId, location_id AS LocationId, opened_at AS OpenedAt,
        last_seen_at AS LastSeenAt, resolved_at AS ResolvedAt, last_notified_at AS LastNotifiedAt,
        resolution_notified_at AS ResolutionNotifiedAt, peak_value AS PeakValue, breach_count AS BreachCount";

    public async Task<Incident?> OpenIncidentAsync(int ruleId, int locationId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<Incident>(new CommandDefinition(
            $"SELECT {IncidentColumns} FROM alert_events WHERE rule_id = @ruleId AND location_id = @locationId AND resolved_at IS NULL",
            new { ruleId, locationId }, cancellationToken: ct));
    }

    /// <summary>Most recent notification for this rule and beach, opened or resolved, used for the cooldown.</summary>
    public async Task<DateTime?> LastNotificationAsync(int ruleId, int locationId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(@"
            SELECT max(greatest(last_notified_at, resolution_notified_at)) FROM alert_events
            WHERE rule_id = @ruleId AND location_id = @locationId", new { ruleId, locationId }, cancellationToken: ct));
    }

    /// <summary>Null when someone else opened the incident first, the partial unique index keeps it to one.</summary>
    public async Task<Incident?> OpenAsync(int ruleId, int locationId, double value, DateTime now, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<Incident>(new CommandDefinition($@"
            INSERT INTO alert_events (rule_id, location_id, opened_at, last_seen_at, peak_value, breach_count)
            VALUES (@ruleId, @locationId, @now, @now, @value, 1)
            ON CONFLICT (rule_id, location_id) WHERE resolved_at IS NULL DO NOTHING
            RETURNING {IncidentColumns}", new { ruleId, locationId, value, now }, cancellationToken: ct));
    }

    public async Task UpdateBreachAsync(long id, double peak, DateTime now, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE alert_events SET last_seen_at = @now, breach_count = breach_count + 1, peak_value = @peak WHERE id = @id",
            new { id, peak, now }, cancellationToken: ct));
    }

    public async Task<Incident?> ResolveAsync(long id, DateTime now, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<Incident>(new CommandDefinition($@"
            UPDATE alert_events SET resolved_at = @now, last_seen_at = @now WHERE id = @id AND resolved_at IS NULL
            RETURNING {IncidentColumns}", new { id, now }, cancellationToken: ct));
    }

    public async Task MarkOpenedNotifiedAsync(long id, DateTime now, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("UPDATE alert_events SET last_notified_at = @now WHERE id = @id",
            new { id, now }, cancellationToken: ct));
    }

    public async Task MarkResolvedNotifiedAsync(long id, DateTime now, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("UPDATE alert_events SET resolution_notified_at = @now WHERE id = @id",
            new { id, now }, cancellationToken: ct));
    }

    /// <summary>Resolved incidents whose people were told about the opening but not yet about the end.</summary>
    public async Task<IReadOnlyList<Incident>> PendingResolutionsAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<Incident>(new CommandDefinition($@"
            SELECT {IncidentColumns} FROM alert_events
            WHERE resolved_at IS NOT NULL AND last_notified_at IS NOT NULL AND resolution_notified_at IS NULL
            ORDER BY resolved_at", cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<Incident>> OpenIncidentsAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<Incident>(new CommandDefinition(
            $"SELECT {IncidentColumns} FROM alert_events WHERE resolved_at IS NULL", cancellationToken: ct))).ToList();
    }

    /// <summary>A rule by id whether or not it is still enabled, so late messages for old incidents can name it.</summary>
    public async Task<AlertRule?> RuleAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        var r = await conn.QueryFirstOrDefaultAsync<RuleRow>(new CommandDefinition(@"
            SELECT id AS Id, name AS Name, metric AS Metric, location_id AS LocationId, rule_type AS RuleType,
                   operator AS Operator, threshold AS Threshold, duration_minutes AS DurationMinutes,
                   severity AS Severity, cooldown_minutes AS CooldownMinutes, enabled AS Enabled
            FROM alert_rules WHERE id = @id", new { id }, cancellationToken: ct));
        return r is null ? null : new AlertRule(r.Id, r.Name, r.Metric, r.LocationId, r.RuleType, r.Operator,
            r.Threshold, r.DurationMinutes, r.Severity, r.CooldownMinutes, r.Enabled);
    }

    public async Task<AlertLocation?> LocationAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<AlertLocation>(new CommandDefinition(
            "SELECT id AS Id, slug AS Slug, name AS Name, coast AS Coast FROM locations WHERE id = @id",
            new { id }, cancellationToken: ct));
    }
}
