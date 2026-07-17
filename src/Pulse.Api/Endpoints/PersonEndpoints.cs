using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pulse.Api.Contracts;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;

namespace Pulse.Api.Endpoints;

public static class PersonEndpoints
{
    public static IEndpointRouteBuilder MapPersonEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{projectId:guid}/persons");

        group.MapGet("/", async (Guid projectId, int? limit, int? offset, PulseDbContext db) =>
        {
            var take = Math.Clamp(limit ?? 100, 1, 500);
            var skip = Math.Max(offset ?? 0, 0);

            var persons = await db.Persons
                .Where(p => p.ProjectId == projectId)
                .OrderBy(p => p.CreatedAt)
                .Skip(skip)
                .Take(take)
                .ToListAsync();

            var responses = new List<PersonResponse>(persons.Count);
            foreach (var person in persons)
            {
                responses.Add(await ToResponseAsync(person, db));
            }

            return Results.Ok(responses);
        });

        group.MapGet("/{personId:guid}", async (Guid projectId, Guid personId, PulseDbContext db) =>
        {
            var person = await db.Persons
                .SingleOrDefaultAsync(p => p.ProjectId == projectId && p.Id == personId);

            return person is null
                ? Results.NotFound()
                : Results.Ok(await ToResponseAsync(person, db));
        });

        group.MapGet("/by-distinct-id/{distinctId}", async (Guid projectId, string distinctId, PulseDbContext db) =>
        {
            var mapping = await db.PersonDistinctIds
                .SingleOrDefaultAsync(m => m.ProjectId == projectId && m.DistinctId == distinctId);

            if (mapping is null)
            {
                return Results.NotFound();
            }

            var person = await db.Persons.SingleAsync(p => p.Id == mapping.PersonId);
            return Results.Ok(await ToResponseAsync(person, db));
        });

        return app;
    }

    private static async Task<PersonResponse> ToResponseAsync(Person person, PulseDbContext db)
    {
        var distinctIds = await db.PersonDistinctIds
            .Where(m => m.PersonId == person.Id)
            .OrderBy(m => m.DistinctId)
            .Select(m => m.DistinctId)
            .ToListAsync();

        return new PersonResponse(
            person.Id,
            person.ProjectId,
            JsonSerializer.Deserialize<JsonElement>(person.PropertiesJson),
            distinctIds,
            person.CreatedAt);
    }
}
