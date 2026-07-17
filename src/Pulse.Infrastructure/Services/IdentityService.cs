using Microsoft.EntityFrameworkCore;
using Pulse.Domain;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

/// <summary>
/// Resolves distinct ids to persons and performs <c>$identify</c> merges.
/// Reads consult the change tracker first so that events earlier in the same
/// batch (not yet flushed) are visible to later ones.
/// </summary>
public class IdentityService
{
    private readonly PulseDbContext _db;

    public IdentityService(PulseDbContext db)
    {
        _db = db;
    }

    /// <summary>Gets the person owning <paramref name="distinctId"/>, creating one if unseen.</summary>
    public async Task<Person> ResolveAsync(Guid projectId, string distinctId, CancellationToken ct = default)
    {
        var person = await FindPersonAsync(projectId, distinctId, ct);
        return person ?? CreatePerson(projectId, distinctId);
    }

    /// <summary>
    /// Links an anonymous distinct id to an identified one. If both already
    /// map to different persons, the anonymous person is merged into the
    /// identified person: distinct ids and events are repointed, properties
    /// merged (identified person wins conflicts), and the loser deleted.
    /// </summary>
    public async Task<Person> IdentifyAsync(
        Guid projectId,
        string identifiedDistinctId,
        string anonDistinctId,
        CancellationToken ct = default)
    {
        var identifiedPerson = await FindPersonAsync(projectId, identifiedDistinctId, ct);
        var anonPerson = await FindPersonAsync(projectId, anonDistinctId, ct);

        if (identifiedPerson is null && anonPerson is null)
        {
            var person = CreatePerson(projectId, identifiedDistinctId);
            AddMapping(projectId, anonDistinctId, person.Id);
            return person;
        }

        if (identifiedPerson is null)
        {
            // Anonymous person gets promoted: alias the identified id onto it.
            AddMapping(projectId, identifiedDistinctId, anonPerson!.Id);
            return anonPerson;
        }

        if (anonPerson is null)
        {
            AddMapping(projectId, anonDistinctId, identifiedPerson.Id);
            return identifiedPerson;
        }

        if (identifiedPerson.Id == anonPerson.Id)
        {
            return identifiedPerson; // Already merged; identify is idempotent.
        }

        return await MergeAsync(winner: identifiedPerson, loser: anonPerson, ct);
    }

    private async Task<Person> MergeAsync(Person winner, Person loser, CancellationToken ct)
    {
        winner.PropertiesJson = PersonPropertyMerger.MergePersons(winner.PropertiesJson, loser.PropertiesJson);

        // Flush pending inserts so the bulk updates below see every row.
        await _db.SaveChangesAsync(ct);

        await _db.PersonDistinctIds
            .Where(m => m.PersonId == loser.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.PersonId, winner.Id), ct);

        await _db.Events
            .Where(e => e.PersonId == loser.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.PersonId, winner.Id), ct);

        // Fix up anything the change tracker still holds in memory.
        foreach (var tracked in _db.PersonDistinctIds.Local.Where(m => m.PersonId == loser.Id).ToList())
        {
            _db.Entry(tracked).State = EntityState.Detached;
        }

        foreach (var tracked in _db.Events.Local.Where(e => e.PersonId == loser.Id).ToList())
        {
            _db.Entry(tracked).State = EntityState.Detached;
        }

        _db.Persons.Remove(loser);
        await _db.SaveChangesAsync(ct);

        return winner;
    }

    private async Task<Person?> FindPersonAsync(Guid projectId, string distinctId, CancellationToken ct)
    {
        var mapping =
            _db.PersonDistinctIds.Local.FirstOrDefault(m => m.ProjectId == projectId && m.DistinctId == distinctId)
            ?? await _db.PersonDistinctIds
                .SingleOrDefaultAsync(m => m.ProjectId == projectId && m.DistinctId == distinctId, ct);

        if (mapping is null)
        {
            return null;
        }

        return _db.Persons.Local.FirstOrDefault(p => p.Id == mapping.PersonId)
               ?? await _db.Persons.SingleAsync(p => p.Id == mapping.PersonId, ct);
    }

    private Person CreatePerson(Guid projectId, string distinctId)
    {
        var person = new Person { ProjectId = projectId };
        _db.Persons.Add(person);
        AddMapping(projectId, distinctId, person.Id);
        return person;
    }

    private void AddMapping(Guid projectId, string distinctId, Guid personId) =>
        _db.PersonDistinctIds.Add(new PersonDistinctId
        {
            ProjectId = projectId,
            DistinctId = distinctId,
            PersonId = personId,
        });
}
