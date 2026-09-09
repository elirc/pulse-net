# People, projects, and tables

**Keep one sentence:** a User manages Pulse; a Person is measured by Pulse.

## The names in our recurring example

| Name | Example | Code |
| --- | --- | --- |
| User | Mara logs into the management API | [User](../src/Pulse.Domain/Entities/User.cs) |
| Project | Mara's shop analytics workspace | [Project](../src/Pulse.Domain/Entities/Project.cs) |
| ProjectMembership | Mara may manage that project | [ProjectMembership](../src/Pulse.Domain/Entities/ProjectMembership.cs) |
| Person | A tracked shop visitor | [Person](../src/Pulse.Domain/Entities/Person.cs) |
| Distinct ID | `device-7`, later perhaps `customer-42` | [PersonDistinctId](../src/Pulse.Domain/Entities/PersonDistinctId.cs) |
| AnalyticsEvent | That visitor viewed the pricing page | [AnalyticsEvent](../src/Pulse.Domain/Entities/AnalyticsEvent.cs) |

Creating Mara's account does not automatically make her a tracked Person.
Sending an event from a visitor does not automatically create a management User.

## IDs are labels, not the objects themselves

`PersonId` is the ID of a Person record. `DistinctId` is an incoming identifier
from a product. The mapping table connects them within a project.

```text
Before identification:
Project P1 + device-7     -> Person A

After identifying customer-42 with device-7:
Project P1 + device-7     -> Person A
Project P1 + customer-42  -> Person A
```

That example assumes `customer-42` has no separate person yet. If both IDs
already point at different people, `IdentityService` merges them, preserves
the identified person's conflicting properties, and repoints historical
events and identifier mappings. Read the branch conditions before assuming
every identify call follows exactly the same path.

## Event properties and person properties

`page: /pricing` describes this event. `plan: pro` may describe a person.
The capture convention updates person data through special properties:

```json
{
  "$set": { "plan": "pro" },
  "$set_once": { "first_referrer": "newsletter" }
}
```

`$set` can overwrite an existing value. `$set_once` fills a missing value.
You can read the pure merging rules in
[PersonPropertyMerger](../src/Pulse.Domain/PersonPropertyMerger.cs).

## How objects connect to the database

[PulseDbContext](../src/Pulse.Infrastructure/PulseDbContext.cs) exposes
collections such as `Persons`, `Events`, and `QueuedEvents`. Its model setup
also defines indexes and conversions. A C# relationship drawn here does not
by itself promise a database-enforced foreign key; inspect that configuration.

**Check:** two `pageview` events map to Person A on one day. How many events
and unique persons should a matching trend count?

<details>
<summary>Answer</summary>

Two events, one unique person. Event count measures occurrences; the unique
person count measures distinct resolved people represented in those events.

</details>

Next: [who is allowed to do what](09-keys-and-permissions.md).
