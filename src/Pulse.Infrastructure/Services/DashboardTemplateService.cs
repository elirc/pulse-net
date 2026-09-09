using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pulse.Domain;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

public record TemplateInsight(string? Ref, string? Name, string? Type, JsonElement? Config);
public record TemplateTile(string? InsightRef, JsonElement? Layout);
public record DashboardTemplate(int Version, string? Name, string? Description,
    List<TemplateInsight?>? Insights, List<TemplateTile?>? Tiles);
public record TemplateOutcome(DashboardTemplate? Template = null, Dashboard? Dashboard = null,
    string? Error = null, bool Missing = false);

/// <summary>A portable graph uses document-local references, never source database identities.</summary>
public sealed class DashboardTemplateService(PulseDbContext db, TimeProvider clock)
{
    public const int MaximumBodyBytes = 256 * 1024;

    public async Task<TemplateOutcome> ExportAsync(Guid projectId, Guid dashboardId, CancellationToken ct)
    {
        var dashboard = await db.Dashboards.AsNoTracking().SingleOrDefaultAsync(d => d.ProjectId == projectId && d.Id == dashboardId, ct);
        if (dashboard is null) return new(Missing: true);
        var tiles = await db.DashboardTiles.AsNoTracking().Where(t => t.DashboardId == dashboardId)
            .OrderBy(t => t.CreatedAt).ThenBy(t => t.Id).Take(101).ToListAsync(ct);
        if (tiles.Count > 100) return new(Error: "Template supports at most 100 tiles.");
        var ids = tiles.Select(t => t.InsightId).Distinct().ToArray();
        if (ids.Length > 50) return new(Error: "Template supports at most 50 insights.");
        var insights = await db.Insights.AsNoTracking().Where(i => i.ProjectId == projectId && ids.Contains(i.Id))
            .OrderBy(i => i.CreatedAt).ThenBy(i => i.Id).ToListAsync(ct);
        if (insights.Count != ids.Length) return new(Error: "A source tile has an unavailable insight.");
        var references = insights.Select((insight, index) => (insight.Id, Reference: $"query-{index + 1}"))
            .ToDictionary(item => item.Id, item => item.Reference);
        DashboardTemplate document;
        try
        {
            document = new(1, dashboard.Name, dashboard.Description,
                insights.Select(i => (TemplateInsight?)new TemplateInsight(references[i.Id], i.Name, i.Type.ToString().ToLowerInvariant(),
                    JsonSerializer.Deserialize<JsonElement>(i.ConfigJson))).ToList(),
                tiles.Select(t => (TemplateTile?)new TemplateTile(references[t.InsightId], JsonSerializer.Deserialize<JsonElement>(t.LayoutJson))).ToList());
        }
        catch (JsonException) { return new(Error: "Source contains invalid stored JSON."); }
        var validation = Validate(document, clock.GetUtcNow());
        if (validation.Error is { } error) return new(Error: error);
        if (JsonSerializer.SerializeToUtf8Bytes(document, JsonSerializerOptions.Web).Length > MaximumBodyBytes)
            return new(Error: "Exported template exceeds 256 KiB.");
        return new(Template: document);
    }

    public async Task<TemplateOutcome> ImportAsync(Guid projectId, DashboardTemplate document, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var validation = Validate(document, now);
        if (validation.Error is { } error) return new(Error: error);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var dashboard = new Dashboard { ProjectId = projectId, Name = document.Name!.Trim(), Description = document.Description?.Trim() ?? "", CreatedAt = now };
        var imported = new Dictionary<string, Insight>(StringComparer.Ordinal);
        foreach (var entry in document.Insights!)
        {
            var config = validation.Configs[entry!.Ref!];
            var insight = new Insight { ProjectId = projectId, Name = entry.Name!.Trim(), Type = config.Type, ConfigJson = config.StorageJson, CreatedAt = now };
            imported.Add(entry.Ref!, insight);
            db.Insights.Add(insight);
        }
        db.Dashboards.Add(dashboard);
        db.DashboardTiles.AddRange(document.Tiles!.Select(tile => new DashboardTile
        {
            DashboardId = dashboard.Id, InsightId = imported[tile!.InsightRef!].Id,
            LayoutJson = tile.Layout!.Value.GetRawText(), CreatedAt = now,
        }));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(Dashboard: dashboard);
    }

    private static (Dictionary<string, ValidatedInsightConfig> Configs, string? Error) Validate(DashboardTemplate document, DateTimeOffset now)
    {
        var configs = new Dictionary<string, ValidatedInsightConfig>(StringComparer.Ordinal);
        (Dictionary<string, ValidatedInsightConfig>, string) Fail(string message) => (configs, message);
        if (document.Version != 1) return Fail("Only template version 1 is supported.");
        if (string.IsNullOrWhiteSpace(document.Name) || document.Name.Trim().Length > 200) return Fail("Dashboard name must contain 1–200 trimmed characters.");
        if ((document.Description?.Trim().Length ?? 0) > 2000) return Fail("Description must not exceed 2000 trimmed characters.");
        if (document.Insights is null || document.Insights.Count > 50) return Fail("Supply an insights array with at most 50 definitions.");
        if (document.Tiles is null || document.Tiles.Count > 100) return Fail("Supply a tiles array with at most 100 entries.");
        foreach (var insight in document.Insights)
        {
            if (insight is null || string.IsNullOrWhiteSpace(insight.Ref)) return Fail("Every insight needs a nonblank document-local ref.");
            if (configs.ContainsKey(insight.Ref)) return Fail("Insight refs must be unique and are case-sensitive.");
            if (string.IsNullOrWhiteSpace(insight.Name) || insight.Name.Trim().Length > 200) return Fail("Each insight name must contain 1–200 trimmed characters.");
            var config = InsightConfigValidator.Validate(insight.Type, insight.Config, now);
            if (!config.IsValid) return Fail($"Insight '{insight.Ref}' has invalid configuration: {string.Join("; ", config.Errors.Select(e => e.Key + ": " + string.Join(", ", e.Value)))}");
            if (config.Filters.Any(filter => filter.Target == FilterTarget.Cohort)) return Fail("Version 1 templates do not support project-local cohort filters.");
            configs.Add(insight.Ref, config);
        }
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tile in document.Tiles)
        {
            if (tile?.InsightRef is not { } reference || !configs.ContainsKey(reference)) return Fail("Every tile must reference an insight definition in this document.");
            if (tile.Layout is not { ValueKind: JsonValueKind.Object }) return Fail("Every tile layout must be an object.");
            referenced.Add(reference);
        }
        if (referenced.Count != configs.Count) return Fail("Every insight definition must be referenced by at least one tile.");
        return (configs, null);
    }
}
