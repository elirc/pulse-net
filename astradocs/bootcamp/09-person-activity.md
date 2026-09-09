# Session 9: one person, many identities, several views

Stories: MID-10 and MID-11. This lesson explains the implementation; consult the
[verification record](verification.md) before treating an example as tested.

## Predict the support engineer's screen

A visitor browses as `device-A`, then signs in as `user-B`. The identity service
merges their records. Support opens the surviving person's timeline. Should the
old browsing event disappear because its `DistinctId` still says `device-A`?

No. Events have two different pieces of identity information. `DistinctId`
preserves the identifier received from the SDK. `PersonId` links the event to
the canonical person after identity resolution and merges. The timeline filters
by project and canonical person; it displays the original distinct ID for context.

Think of two email addresses belonging to one customer. Counting addresses
does not count customers, and searching only the newest address misses history.
In database terms, aliases are mappings and the event's person ID is the join
point. In API terms, the timeline URL names the canonical person, not an alias.

## Follow the code in both directions

Start with [PersonActivityEndpoints](../../src/Pulse.Api/Endpoints/PersonActivityEndpoints.cs).
Read the access guard, project-scoped person existence check, query validation,
service call, and response. Then open
[PersonActivityService](../../src/Pulse.Infrastructure/Services/PersonActivityService.cs)
and locate the two predicates shared by its timeline and summary: project ID
and person ID. Finally inspect
[IdentityService](../../src/Pulse.Infrastructure/Services/IdentityService.cs)
to find where a merge repoints old events.

Now reverse the explanation: a merge updates the stored event relationship;
the query selects that relationship; the endpoint exposes the selected rows to
an authorized project viewer. Repeating a trace backwards helps reveal places
where an assumption was never actually enforced.

## Timeline pagination with ties

Suppose the rows, newest first, are:

| Timestamp | ID | Event |
| --- | --- | --- |
| 10:00 | C | signup |
| 10:00 | B | signup |
| 10:00 | A | signup |
| 09:00 | D | browse |

With a page size of one, the first page ends at `(10:00,C)`. A cursor storing
only `10:00` cannot distinguish B from C. The next query needs rows whose time
is earlier, **or whose time is equal and ID is smaller**. Its sorting uses the
same pair in descending order. That returns B next, then A, then D.

The ID comparison must agree with the database's ordering. These tests use
real SQLite, including explicit tied timestamps and IDs; an in-memory C# sort
alone would not verify the generated SQL's continuation behavior.

The service fetches `limit + 1`. The extra row answers “is there another page?”
It removes that extra row from the response and constructs the next position
from the last row actually returned. Pointing past the extra row would skip it.

## What a cursor proves

[ScopedCursor](../../src/Pulse.Infrastructure/Services/ScopedCursor.cs) encodes a
version, purpose, project, person, event filter, timestamp ticks, and event ID.
The endpoint verifies all of that context before using the position. A cursor
from a signup timeline cannot be reused as a purchase timeline position, and
an export cursor has the wrong purpose. Input length is capped at 2,048 characters;
malformed base64url/JSON and unsupported ticks are invalid input.

The encoding does not make the cursor secret, signed, or privileged. A caller
can construct another valid position inside their own authorized query. That
is acceptable: the cursor chooses a position, while the normal membership and
project predicates enforce access. Never remove those predicates because a
cursor already contains a project ID.

A cursor also does not freeze the dataset. A new event inserted ahead of the
current position appears when the caller restarts at page one. A later insert
behind the cursor can appear on a later page. The exactly-once pagination test
describes an unchanged fixture, not a snapshot guarantee during concurrent writes.

## A summary answers different questions

`GET /api/projects/P/persons/U/activity-summary?from=...&to=...` summarizes
processed events in `[from,to)`, with a nonempty range of at most 90 days.

| Metric | Meaning | Common incorrect substitute |
| --- | --- | --- |
| totalEvents | Matching processed event rows | Number of aliases |
| activeUtcDays | Distinct UTC dates among matching timestamps | Local calendar dates |
| firstEventAt | Earliest matching event time, or null | Person's creation time |
| lastEventAt | Latest matching event time, or null | Current time |
| topEvents | Up to five names by count, then ordinal name | First five events |

An event exactly at `from` belongs to the range. An event exactly at `to` does
not. Adjacent summaries can therefore meet at one boundary without counting a
boundary event twice. This repeats session 7's half-open period concept in a
person-centered feature.

Consider `2026-03-02T00:30:00+02:00`: its UTC time is March 1 at 22:30. It is
activity on March 1 for `activeUtcDays`. The user's local midnight is not the
definition of this metric. A date label should express the definition clearly.

## No events is not the same as no person

An existing person without matching activity returns zero counts, null first
and last timestamps, and an empty list. A missing person returns 404. If you
skip the person existence query and merely aggregate events, both cases look
empty. That loses useful product information and can hide a scoping bug.

The same rule applies to a foreign person ID under an otherwise accessible
project: return 404. Access to project P does not imply access to any person ID
supplied after P in a URL.

## Query cost and consistency are part of the feature

Counts and event-name grouping execute in SQL. The active-day calculation loads
only timestamps because the existing UTC-tick converter does not provide a
straightforward translated UTC-date projection. It therefore uses memory
proportional to matching events. It avoids property documents but is not a
constant-memory operation. A 90-day range bounds time, not event volume.

The service runs its database operations sequentially on the scoped context.
It does not claim that count, minimum, maximum, and top names were read from a
single frozen snapshot while ingestion continued. This is a live summary. If
the product later requires one consistent analytical snapshot, that must be
implemented and tested as an additional contract.

## Practice and teach back

1. Draw the two aliases, their surviving person, and two historical events.
   Label which values change during a merge and which original values remain.
2. Walk the four-row table above using page size two. Write the exact next
   predicate before reading the service method.
3. Change a cursor's event filter. Predict 400, then find the check responsible.
4. Compare an empty person with an unknown ID. Explain why the endpoint needs
   an existence query before returning a successful empty result.
5. Convert the offset timestamp to UTC yourself. Explain why counting local
   dates would make results depend on a different definition.
6. Open [PersonActivityTests](../../tests/Pulse.Tests/Api/PersonActivityTests.cs).
   Find the real identity merge, not merely two rows manually sharing an ID.
7. Explain which part of the summary can consume memory proportional to event
   volume. Suggest what measurement you would collect before optimizing it.

Tomorrow, explain this feature without using the word “cursor”: describe what
the client sends to tell the server where to continue. Then give the precise
technical term. Moving between ordinary and technical language tests whether
you understand the mechanism rather than just recognizing its name.
