using System.Text.RegularExpressions;
using CapeCoast.Core.Domain;
using CapeCoast.Infrastructure.Data;

namespace CapeCoast.Api.Endpoints;

public record AlertRuleRequest(string? Name, string? Metric, int? LocationId, string? RuleType, string? Operator,
    double? Threshold, int? DurationMinutes, string? Severity, int? CooldownMinutes, bool? Enabled);

public record MaintenanceWindowRequest(int? LocationId, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, string? Reason);

public record LocationRequest(string? Name, string? Slug, string? Coast, string? CitySiteCode, double? Lon, double? Lat, bool? Active);

public static partial class AdminEndpoints
{
    private static readonly string[] Operators = { ">", ">=", "<", "<=" };
    private static readonly string[] RuleTypes = { "static", "baseline" };
    private static readonly string[] Severities = { "info", "warning", "critical" };
    private static readonly string[] Coasts = { "Atlantic", "False Bay" };

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex("^[A-Z]{1,4}[0-9]{2}[A-Z]?$")]
    private static partial Regex SiteCodePattern();

    public static void MapAdminEndpoints(this WebApplication app)
    {
        var read = app.MapGroup("/api/admin").WithTags("Admin")
            .RequireAuthorization(Roles.ViewerPolicy).RequireRateLimiting(RateLimits.Admin);
        var write = app.MapGroup("/api/admin").WithTags("Admin")
            .RequireAuthorization(Roles.AdminPolicy).RequireRateLimiting(RateLimits.Admin);

        // alert rules
        read.MapGet("/alert-rules", async (AdminRepository repo, CancellationToken ct) => Results.Json(await repo.RulesAsync(ct)))
            .WithSummary("List alert rules (Viewer or Admin)");
        write.MapPost("/alert-rules", CreateRule).WithSummary("Create an alert rule (Admin)");
        write.MapPut("/alert-rules/{id:int}", UpdateRule).WithSummary("Replace an alert rule (Admin)");
        write.MapDelete("/alert-rules/{id:int}", async (int id, AdminRepository repo, CancellationToken ct) =>
            await repo.DeleteRuleAsync(id, ct) ? Results.NoContent() : Results.NotFound()).WithSummary("Delete an alert rule and its events (Admin)");

        // maintenance windows
        read.MapGet("/maintenance-windows", async (AdminRepository repo, CancellationToken ct) => Results.Json(await repo.WindowsAsync(ct)))
            .WithSummary("List maintenance windows (Viewer or Admin)");
        write.MapPost("/maintenance-windows", CreateWindow).WithSummary("Create a maintenance window that suppresses alerts (Admin)");
        write.MapDelete("/maintenance-windows/{id:int}", async (int id, AdminRepository repo, CancellationToken ct) =>
            await repo.DeleteWindowAsync(id, ct) ? Results.NoContent() : Results.NotFound()).WithSummary("Delete a maintenance window (Admin)");

        // locations
        read.MapGet("/locations", async (AdminRepository repo, CancellationToken ct) => Results.Json(await repo.LocationsAsync(ct)))
            .WithSummary("List all locations including inactive ones (Viewer or Admin)");
        write.MapPost("/locations", CreateLocation).WithSummary("Add a beach (Admin)");
        write.MapPut("/locations/{id:int}", UpdateLocation).WithSummary("Update a beach (Admin)");
    }

    // alert rules

    private static async Task<IResult> CreateRule(AlertRuleRequest request, AdminRepository repo, CancellationToken ct)
    {
        var (row, errors) = await ValidateRule(request, 0, repo, ct);
        if (errors.Count > 0) return Results.ValidationProblem(errors);
        var created = await repo.CreateRuleAsync(row!, ct);
        return Results.Created($"/api/admin/alert-rules/{created.Id}", created);
    }

    private static async Task<IResult> UpdateRule(int id, AlertRuleRequest request, AdminRepository repo, CancellationToken ct)
    {
        var (row, errors) = await ValidateRule(request, id, repo, ct);
        if (errors.Count > 0) return Results.ValidationProblem(errors);
        var updated = await repo.UpdateRuleAsync(row!, ct);
        return updated is null ? Results.NotFound() : Results.Json(updated);
    }

    private static async Task<(AlertRuleRow?, Dictionary<string, string[]>)> ValidateRule(
        AlertRuleRequest r, int id, AdminRepository repo, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();

        var name = r.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100) errors["name"] = new[] { "name is required, up to 100 characters" };

        if (r.Metric is null || !MetricCatalog.IsAlertMetric(r.Metric))
            errors["metric"] = new[] { "unknown metric, use enterococci or one from /api/metrics" };

        var ruleType = r.RuleType ?? "static";
        if (!RuleTypes.Contains(ruleType)) errors["ruleType"] = new[] { "ruleType must be static or baseline" };

        if (r.Operator is null || !Operators.Contains(r.Operator)) errors["operator"] = new[] { "operator must be >, >=, < or <=" };

        if (r.Threshold is null || double.IsNaN(r.Threshold.Value) || double.IsInfinity(r.Threshold.Value))
            errors["threshold"] = new[] { "threshold must be a number" };
        else if (ruleType == "baseline" && r.Threshold is <= 0 or > 1000)
            errors["threshold"] = new[] { "a baseline threshold is a percentage deviation between 0 and 1000" };

        var severity = r.Severity ?? "warning";
        if (!Severities.Contains(severity)) errors["severity"] = new[] { "severity must be info, warning or critical" };

        var duration = r.DurationMinutes ?? 0;
        if (duration is < 0 or > 10080) errors["durationMinutes"] = new[] { "durationMinutes must be between 0 and 10080" };

        var cooldown = r.CooldownMinutes ?? 60;
        if (cooldown is < 0 or > 10080) errors["cooldownMinutes"] = new[] { "cooldownMinutes must be between 0 and 10080" };

        if (r.LocationId is not null && !await repo.LocationExistsAsync(r.LocationId.Value, ct))
            errors["locationId"] = new[] { "no such location" };

        if (errors.Count > 0) return (null, errors);

        return (new AlertRuleRow
        {
            Id = id, Name = name!, Metric = r.Metric!, LocationId = r.LocationId, RuleType = ruleType,
            Operator = r.Operator!, Threshold = r.Threshold!.Value, DurationMinutes = duration,
            Severity = severity, CooldownMinutes = cooldown, Enabled = r.Enabled ?? true,
        }, errors);
    }

    // maintenance windows

    private static async Task<IResult> CreateWindow(MaintenanceWindowRequest r, AdminRepository repo, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();

        if (r.StartsAt is null) errors["startsAt"] = new[] { "startsAt is required" };
        if (r.EndsAt is null) errors["endsAt"] = new[] { "endsAt is required" };
        if (r.StartsAt is not null && r.EndsAt is not null)
        {
            if (r.EndsAt <= r.StartsAt) errors["endsAt"] = new[] { "endsAt must be after startsAt" };
            else if (r.EndsAt - r.StartsAt > TimeSpan.FromDays(366)) errors["endsAt"] = new[] { "a window can last at most a year" };
        }
        if (r.Reason is { Length: > 200 }) errors["reason"] = new[] { "reason is limited to 200 characters" };
        if (r.LocationId is not null && !await repo.LocationExistsAsync(r.LocationId.Value, ct))
            errors["locationId"] = new[] { "no such location" };

        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var created = await repo.CreateWindowAsync(new MaintenanceWindowRow
        {
            LocationId = r.LocationId,
            StartsAt = r.StartsAt!.Value.UtcDateTime,
            EndsAt = r.EndsAt!.Value.UtcDateTime,
            Reason = string.IsNullOrWhiteSpace(r.Reason) ? null : r.Reason.Trim(),
        }, ct);
        return Results.Created($"/api/admin/maintenance-windows/{created.Id}", created);
    }

    // locations

    private static Dictionary<string, string[]> ValidateLocation(LocationRequest r, bool creating)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 100) errors["name"] = new[] { "name is required, up to 100 characters" };
        if (creating && (r.Slug is null || r.Slug.Length > 60 || !SlugPattern().IsMatch(r.Slug)))
            errors["slug"] = new[] { "slug must be lowercase letters, digits and hyphens" };
        if (r.Coast is null || !Coasts.Contains(r.Coast)) errors["coast"] = new[] { "coast must be Atlantic or False Bay" };
        if (r.CitySiteCode is not null && !SiteCodePattern().IsMatch(r.CitySiteCode))
            errors["citySiteCode"] = new[] { "citySiteCode looks like CN09 or XCS26" };
        if (r.Lat is null or < -90 or > 90) errors["lat"] = new[] { "lat must be between -90 and 90" };
        if (r.Lon is null or < -180 or > 180) errors["lon"] = new[] { "lon must be between -180 and 180" };
        return errors;
    }

    private static async Task<IResult> CreateLocation(LocationRequest r, AdminRepository repo, CancellationToken ct)
    {
        var errors = ValidateLocation(r, creating: true);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var created = await repo.CreateLocationAsync(new LocationAdminRow
        {
            Name = r.Name!.Trim(), Slug = r.Slug!, Coast = r.Coast!, CitySiteCode = r.CitySiteCode,
            Lon = r.Lon!.Value, Lat = r.Lat!.Value, Active = r.Active ?? true,
        }, ct);

        return created is null
            ? Results.Conflict(new { error = "slug or city site code is already in use" })
            : Results.Created($"/api/admin/locations/{created.Id}", created);
    }

    private static async Task<IResult> UpdateLocation(int id, LocationRequest r, AdminRepository repo, CancellationToken ct)
    {
        var errors = ValidateLocation(r, creating: false);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var updated = await repo.UpdateLocationAsync(new LocationAdminRow
        {
            Id = id, Name = r.Name!.Trim(), Coast = r.Coast!, CitySiteCode = r.CitySiteCode,
            Lon = r.Lon!.Value, Lat = r.Lat!.Value, Active = r.Active ?? true,
        }, ct);

        if (updated is not null) return Results.Json(updated);
        return await repo.LocationExistsAsync(id, ct)
            ? Results.Conflict(new { error = "city site code is already in use" })
            : Results.NotFound();
    }
}
