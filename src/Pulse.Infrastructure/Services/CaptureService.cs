using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pulse.Domain;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

/// <summary>One event to ingest, already unwrapped from single/batch shape.</summary>
public record IncomingEvent(
    string Name,
    string DistinctId,
    DateTimeOffset? Timestamp,
    string PropertiesJson);

public record CaptureResult(int Ingested);

/// <summary>
/// Ingestion pipeline: authenticates the project by API key, resolves each
/// event's distinct id to a person (handling <c>$identify</c> merges and
/// <c>$set</c>/<c>$set_once</c> person properties), and persists events.
/// The whole payload is written in one transaction — one bad event rejects
/// everything, so SDKs can safely retry.
/// </summary>
public class CaptureService
{
    public const string IdentifyEventName = "$identify";
    public const string AnonDistinctIdProperty = "$anon_distinct_id";
    public const string SetProperty = "$set";
    public const string SetOnceProperty = "$set_once";

    private readonly PulseDbContext _db;
    private readonly IdentityService _identity;
    private readonly TimeProvider _clock;

    public CaptureService(PulseDbContext db, IdentityService identity, TimeProvider clock)
    {
        _db = db;
        _identity = identity;
        _clock = clock;
    }

    public async Task<Project?> FindProjectByApiKeyAsync(string apiKey, CancellationToken ct = default) =>
        await _db.Projects.SingleOrDefaultAsync(p => p.ApiKey == apiKey, ct);

    public async Task<CaptureResult> IngestAsync(
        Project project,
        IReadOnlyList<IncomingEvent> events,
        CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        foreach (var incoming in events)
        {
            using var properties = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(incoming.PropertiesJson) ? "{}" : incoming.PropertiesJson);
            var root = properties.RootElement;

            var person = await ResolvePersonAsync(project.Id, incoming, root, ct);
            ApplyPersonProperties(person, root);
            await UpsertDefinitionsAsync(project.Id, incoming.Name, root, now, ct);

            _db.Events.Add(new AnalyticsEvent
            {
                ProjectId = project.Id,
                Name = incoming.Name,
                DistinctId = incoming.DistinctId,
                PersonId = person.Id,
                Timestamp = incoming.Timestamp ?? now,
                PropertiesJson = incoming.PropertiesJson,
            });
        }

        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new CaptureResult(events.Count);
    }

    private async Task<Person> ResolvePersonAsync(
        Guid projectId,
        IncomingEvent incoming,
        JsonElement properties,
        CancellationToken ct)
    {
        if (incoming.Name == IdentifyEventName
            && properties.ValueKind == JsonValueKind.Object
            && properties.TryGetProperty(AnonDistinctIdProperty, out var anon)
            && anon.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(anon.GetString()))
        {
            return await _identity.IdentifyAsync(projectId, incoming.DistinctId, anon.GetString()!, ct);
        }

        return await _identity.ResolveAsync(projectId, incoming.DistinctId, ct);
    }

    /// <summary>
    /// Keeps the event/property registries current: every ingested event
    /// upserts its name and its non-system property keys (with the JSON kind
    /// first observed for each key).
    /// </summary>
    private async Task UpsertDefinitionsAsync(
        Guid projectId,
        string eventName,
        JsonElement properties,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var eventDef =
            _db.EventDefinitions.Local.FirstOrDefault(d => d.ProjectId == projectId && d.Name == eventName)
            ?? await _db.EventDefinitions
                .SingleOrDefaultAsync(d => d.ProjectId == projectId && d.Name == eventName, ct);

        if (eventDef is null)
        {
            _db.EventDefinitions.Add(new EventDefinition
            {
                ProjectId = projectId,
                Name = eventName,
                FirstSeenAt = now,
                LastSeenAt = now,
            });
        }
        else
        {
            eventDef.LastSeenAt = now;
        }

        if (properties.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in properties.EnumerateObject())
        {
            if (property.Name.StartsWith('$') || property.Value.ValueKind is JsonValueKind.Null)
            {
                continue; // System keys ($set, $anon_distinct_id, ...) aren't user properties.
            }

            var propertyDef =
                _db.PropertyDefinitions.Local.FirstOrDefault(d => d.ProjectId == projectId && d.Name == property.Name)
                ?? await _db.PropertyDefinitions
                    .SingleOrDefaultAsync(d => d.ProjectId == projectId && d.Name == property.Name, ct);

            if (propertyDef is null)
            {
                _db.PropertyDefinitions.Add(new PropertyDefinition
                {
                    ProjectId = projectId,
                    Name = property.Name,
                    PropertyType = KindName(property.Value.ValueKind),
                    FirstSeenAt = now,
                    LastSeenAt = now,
                });
            }
            else
            {
                propertyDef.LastSeenAt = now;
            }
        }
    }

    private static string KindName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Array => "array",
        _ => "object",
    };

    private static void ApplyPersonProperties(Person person, JsonElement properties)
    {
        if (properties.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        JsonElement? set = properties.TryGetProperty(SetProperty, out var s) ? s : null;
        JsonElement? setOnce = properties.TryGetProperty(SetOnceProperty, out var so) ? so : null;

        if (set is not null || setOnce is not null)
        {
            person.PropertiesJson = PersonPropertyMerger.Apply(person.PropertiesJson, set, setOnce);
        }
    }
}
