# Session 14: make a small overview tell the truth

Stories: MID-27 through MID-30. Consult the [ledger](stories.json) for completed
checks. These features teach a deceptively hard skill: giving every displayed
number a precise meaning.

## A registry is not an activity report

An event registry lists names observed by instrumentation. An event-usage report
counts processed Events inside a requested range. A name can remain registered
after all its recent events are gone. Both views can be correct because they
answer different questions.

Suppose this week contains three signup events and one purchase. With a top-list
limit of one, usage returns one item, signup=3, but totalEvents remains four and
eventNameCount remains two. Computing totals from the returned top item would
silently change the meaning of “total.”

[ProjectDiscoveryService](../../src/Pulse.Infrastructure/Services/ProjectDiscoveryService.cs)
groups count/min/max in SQL and materializes only compact event-name groups,
without event properties. It orders those groups by count and .NET ordinal name
before taking the requested top limit. This uses memory proportional to distinct
matching names. The explicit .NET ordering also avoids assuming SQLite's UTF-8
BINARY collation is identical for every Unicode string to UTF-16 ordinal comparison.

The time range is `[from,to)`, nonempty and at most 90 days. This repeats the
half-open boundary from comparison and person summary: `from` is included,
`to` is excluded. Queued and dead-letter envelopes are not processed occurrences.
Stored system events such as `$identify` count like other processed events.

## Property values need typed categories

For a literal top-level property named `plan`, these values are different:

| Stored JSON | Category |
| --- | --- |
| `{}` | Missing |
| `{"plan":null}` | Null |
| `{"plan":""}` | Empty string value |
| `{"plan":"null"}` | The four-character string `null` |
| `{"plan":1}` | Non-string |
| `{"plan":"1"}` | String value `1` |
| `{"plan":"(other)"}` | An ordinary string value |

Stringifying everything into display labels would merge meaningfully different
inputs. Property discovery keeps exact, case-sensitive string counts and separate
missing/null/non-string counters. Omitted string frequencies contribute to the
numeric `otherStringCount`, rather than a synthetic value that can collide with
the real string `(other)`.

The counts reconcile:

`totalEvents = returned string counts + otherStringCount + missingCount + nullCount + nonStringCount`

Property names are literal top-level keys. `plan.name` means a key containing a
dot, not traversal into a nested object. Declaring that limitation avoids silently
borrowing a different query language from another part of the application.

## A small response does not imply bounded input work

Finding the ten most common values may require examining thousands of events.
The endpoint caps its time range at 31 days and its output at 25 values, but it
also needs input-work limits: 10,000 matching rows and 8 MiB of scanned UTF-8
properties. It reads at most 10,001 rows to detect excess volume.

When either cap is exceeded, the response is 422 with a suggestion to narrow
the range. It returns no partial statistics. Otherwise a result computed from
only the first 10,000 rows could masquerade as the full distribution.

The scan must read a stored string before measuring its size. Therefore the
byte cap does not prevent allocation of one oversized stored row. Malformed
stored JSON, or a non-object root, returns controlled 409 without echoing private
payloads or partial totals. These limits describe bounded work honestly rather
than claiming complete protection from every large allocation.

[DiscoveryBudgetTests](../../tests/Pulse.Tests/Infrastructure/DiscoveryBudgetTests.cs)
tests excess rows, excess bytes, invalid JSON, and non-object properties. Each
failure must have no result object; a narrower empty slice must succeed.

## Ingestion status is a classification of observations

[IngestionStatusPolicy](../../src/Pulse.Infrastructure/Services/IngestionStatusPolicy.cs)
is pure: metrics plus thresholds plus observation time produce status and reasons.
Default thresholds are 1,000 pending rows and 60 seconds oldest pending age.
Equality triggers attention. Reasons appear in this fixed order:

1. `dead_letters_present`: one or more failed envelopes need inspection.
2. `queue_depth_high`: pending count is at least the selected threshold.
3. `oldest_pending_too_old`: nonnull oldest age meets its threshold.

The endpoint returns 200 for both `ok` and `attention`, because it successfully
computed an operational observation. A null oldest age means no pending row
exists. Future enqueue timestamps retain a nonnegative age of zero. One captured
clock value labels the observation and computes its ages.

An empty queue can coexist with a stopped worker and still produce `ok`. This
feature does not observe worker heartbeats. “No observed backlog or dead letters
cross these thresholds” is a defensible statement; “the worker is definitely
alive” is not. This distinction is valuable far beyond this repository.

## Project overview composes definitions, not whole lists

The overview returns safe project metadata, an observation time, counts, and
project ingestion metrics. It uses dedicated DTOs rather than a project response
that includes credentials. SQL projects only ID/name/creation time for the project.

| Count | What is counted |
| --- | --- |
| persons | Stored Person rows, not aliases |
| events | Processed event rows, not queued work |
| insights | Saved definitions |
| dashboards | Boards, not tiles |
| cohorts | Saved audiences |
| featureFlags | Stored flags |
| activeFeatureFlags | Flags whose Active field is true, including 0% rollout |
| completedExports | Jobs in Completed state, not all attempts |

The service uses scoped SQL counts sequentially on one DbContext. Calling list
endpoints and counting their first page would undercount larger projects. Loading
entire objects would retrieve unnecessary properties and documents.

The counts are separate live reads. `observedAt` labels the observation; it does
not guarantee every table was frozen at exactly that instant during ingestion.
This repeats the person-summary consistency limitation in a broader feature.

[ProjectDiscoveryTests](../../tests/Pulse.Tests/Api/ProjectDiscoveryTests.cs) uses
two people with three aliases, one board with two tiles, and an active flag with
zero rollout. Those deliberate differences catch common counting mistakes.
It inspects serialized JSON to ensure project keys and inline export content
are absent, including when the caller is an administrator.

## Practice ladder

1. Explain why a registry can list 20 event names while this week's usage lists
   only three. Identify the different tables queried by each view.
2. Compute the full total and top-one output for three signup and one purchase.
   Point out which value must not shrink when limit changes.
3. Classify every row in the property table before reading the implementation.
   Explain why the string `1` cannot join the numeric value 1.
4. Reconcile a response using the count equation. Deliberately omit one category
   and notice how the arithmetic exposes the mistake.
5. Explain why top-10 output needs a separate scanned-row budget.
6. Predict status for pending=2, age=60, deadLetters=0. Explain why that is
   attention without claiming the worker has stopped.
7. Explain why an active 0% flag increments activeFeatureFlags even though it
   returns false for every rollout decision.

Teach back tomorrow: choose one count on the overview and state its source,
scope, time semantics, exclusions, and consistency limit in five short sentences.
