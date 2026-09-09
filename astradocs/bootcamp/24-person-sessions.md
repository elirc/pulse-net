# Lesson 24: sessions are a window-local interpretation

Story: SR-19. Read [bounded trends](23-bounded-trends.md) first for the shared budget vocabulary and [person activity](09-person-activity.md) for canonical identity.

## What you are learning

Pulse stores events, not session rows. A session response is derived from a selected window, the person's current canonical identity, and an explicit inactivity gap. Change any of those inputs and the apparent sessions can change without editing a session record, because no such record exists.

The route requires `[from,to)` of at most seven days and a gap from 1 through 120 minutes. It reads at most 10,000 events and produces at most 500 sessions. It never loads event properties. Cap excess returns 422 rather than a prefix that looks complete.

## Work the example by hand

For a 30-minute gap, draw events at 10:00, 10:10, and 10:40.

```text
window start                                           window end
|  10:00 -------- 10 min -------- 10:10 ---- 30 min ---- 10:40  |
|       session 1: count 2, observed 600 s       | session 2     |
```

A gap greater than or equal to the threshold starts a new session. Therefore the 30-minute gap splits. A 29:59 gap does not. Events are ordered by timestamp and then GUID, so tied timestamps have stable first and last IDs. Each event belongs to exactly one result.

Session 1 has ordinal 1, its first and last timestamps and IDs, count 2, and observedDurationSeconds 600. Session 2 has ordinal 2, count 1, and duration 0. Ordinals belong only to this response; a late event can regroup later responses.

## Half-open time and conservative flags

An event exactly at `from` is included. An event exactly at `to` is excluded. No event before or after the window is read. As a result, the service cannot know whether the first returned session joins earlier activity or the last joins later activity. It sets `mayStartBeforeWindow` on the first and `mayContinueAfterWindow` on the last even when their events are far from the boundaries.

These flags express uncertainty, not a claim that neighboring events exist. Move `from` from 09:00 to 10:05 during a visit. The 10:00 event disappears, the first timestamp becomes 10:10, and observed duration becomes shorter. Calling that value total visit duration would overstate what was observed.

## Current identity changes history's grouping

The SQL predicate uses the event's current PersonId, not a list of distinct-ID aliases. When the identity service merges two people, it repoints their events to one canonical person. A later sessions request can therefore include both histories. This is intentional current-canonical-identity behavior; it avoids N alias queries and reflects the system's present identity model.

ProjectId remains in both the person existence check and event predicate. A person in another project returns 404 and can never pull cross-project events. An existing local person with no events returns a successful empty list.

## Where each rule belongs

[SessionGrouping.cs](../../src/Pulse.Domain/SessionGrouping.cs) contains only ordered positions, gap arithmetic, output accounting, and result construction. It knows neither EF nor HTTP. [PersonSessionService.cs](../../src/Pulse.Infrastructure/Services/PersonSessionService.cs) proves project ownership, builds the half-open SQL query, projects IDs and timestamps, applies row/deadline budgets, then calls the grouper. [BoundedAnalyticsEndpoints.cs](../../src/Pulse.Api/Endpoints/BoundedAnalyticsEndpoints.cs) validates transport inputs and maps domain capacity failures to 422 and server deadlines to 504.

This split gives three independent questions: Is the grouping math correct? Did persistence select the right events? Did HTTP communicate the outcome honestly?

## Resource and response contract

The response shape is `{personId,from,to,gapMinutes,boundarySemantics:"window-local",sessions}`. There are no event or person properties. A row cap failure is `row_limit`; more than 500 derived groups is `session_limit`. The two-second cooperative deadline is separate from client cancellation in exactly the same way as the bounded trend route.

Materializing up to 10,000 tiny event positions is bounded and makes the pure grouping step easy to audit. A future streaming grouper could reduce retained memory further, but it must still withhold the 200 response until the final session and every cap are known.

## Experiments

1. Run [SessionGroupingTests.cs](../../tests/Pulse.Tests/Domain/SessionGroupingTests.cs). Change 10:40 to 10:39:59 and predict the count and duration. Restore the fixture and swap the IDs of tied events; explain which response fields change.
2. Insert events one tick before `from`, exactly at `from`, one tick before `to`, and exactly at `to`. Predict the included IDs. Verify that outside events do not affect grouping or flags.
3. Query the same visit with `from` at 09:00 and 10:05. Compare observed duration and explain why both results can be correct. Never infer unseen time from either response.
4. Create two identities with activity, merge them through the real identity service, and query the surviving person. Draw before and after timelines. Confirm the query selects one canonical PersonId rather than querying aliases.
5. Generate 501 sessions by spacing events exactly at the gap. Predict a 422 with no partial array. Then use 10,001 events inside one session to show the independent row cap.

## Review questions

Why is the end exclusive while trend's end remains inclusive? What does each conservative flag say, and what does it refuse to say? Why are response ordinals not durable IDs? Which SQL columns prove that properties cannot dominate session-query memory? How can an identity merge change a past period without updating session rows?

Answer once with the timeline, once with the three-layer code path, and again next week from the perspective of an API consumer deciding what labels are honest in a support tool.
