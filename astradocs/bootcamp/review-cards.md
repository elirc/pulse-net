# Review cards: explain, predict, and locate

Use these alongside the 25 bootcamp sessions. Cover the answer and predict first,
then read it and find the corresponding code or test. An analogy helps you start;
the code and evidence help you check it.

## Practice tools

**1. Why did the demo test use `checkout & pay`?**

It exercises URL encoding. An unencoded `&` can separate query parameters,
changing the request's meaning. Find `EscapeDataString` in the demo script.

**2. Why test a batch containing exactly one event?**

Some languages and serializers treat one item differently from a collection.
The endpoint still needs an array. Find the PowerShell array construction and
compare the live one-event result with the default two-event run.

**3. Does zero pending queue work prove successful ingestion?**

No. Work may have moved to dead-letter storage. The demo checks dead letters
and the resulting trend as separate observations.

**4. What must a walkthrough reset change besides visible text?**

Scenario/step state, quiz visibility and answer state, and keyboard focus all
matter. Find the browser-check evidence and distinguish them from glossary
disclosure state, which the story deliberately preserves.

## Scoped queries

**5. Why does filtering need to happen before pagination?**

Paging first can remove the only matching row before the filter sees it.
Construct a three-row example where the match is third and the page size is two.

**6. Why add an ID to a timestamp sort?**

Several rows can share a timestamp. The ID supplies deterministic tie-breaking
for a stable ordering. It does not freeze the dataset against later writes.

**7. What is the difference between an inclusive end and an exclusive end?**

An inclusive end includes rows exactly at that instant; an exclusive end does
not. Name one Pulse route of each kind and read its comparison operator.

**8. Why can a person with several aliases still appear only once?**

The query asks whether any alias matches while selecting person rows. A raw
one-to-many join can duplicate the selected person if several mappings match.

## Database upgrades

**9. Why does adding a C# property not complete a schema feature?**

The persisted database still has its earlier structure. A migration describes
the transition; startup/model code alone does not update existing files.

**10. What does legacy adoption establish?**

It verifies that a file's structure matches the supported baseline before
recording that baseline in migration history. Merely finding a Projects table
would be insufficient evidence.

**11. Why inspect again inside adoption's write transaction?**

Another writer must not change the checked structure between the check and
the history write. The lock protects that interval; the deployment still needs
an explicit stop-writers procedure.

**12. What did the first rejected-adoption test expose?**

Rollback alone did not clear EF's transaction enlistment wrapper. Continuing
with inspection through the same context exposed stale in-memory state.
Check the journal for the fix and its latest verification evidence.

## Dashboard composition

**13. A dashboard with two tiles sharing one insight is duplicated. How many
new rows should appear?**

One dashboard and two tiles, with zero new insights. Draw the four tile arrows
to the one shared insight after duplication.

**14. Nine layouts are valid and one is invalid. How many are saved?**

Zero under the strict batch-layout contract. Validation covers the entire input
before assignment, and successful persistence occurs in one transaction.

**15. Why can two response entries share a query result?**

They represent different tiles with different IDs and layouts that reference
the same insight. Tile identity and query execution are different things.

**16. Why does the selected-refresh result dictionary live inside the request?**

The reuse promise applies to that response. A future request must recompute
against current data. A longer-lived cache would require an invalidation policy.

## Insight lifecycle

**17. What is wrong with saving a preview and deleting it afterward?**

It introduces observable writes and a failure window between them. An untracked
temporary insight can execute the same query without either write.

**18. Why return separate storage and run configurations?**

Storage should retain omitted relative dates; a run needs resolved dates at a
captured time. Saving run config would freeze a supposedly rolling query.

**19. Does a usage GET showing zero references make a later delete safe?**

No. A tile can appear after that observation. Delete must enforce the invariant
inside its own transaction, and tile creation must protect its corresponding
existence check and insertion.

**20. Why does a completed download survive editing its source insight?**

It stores rendered output. The insight stores a recipe. Editing the recipe
changes future executions, not previously stored output.

## Portable templates

**21. Why does `query-1` belong in a template instead of the source insight GUID?**

It names a definition within the document. Import maps that name to a fresh
destination insight ID and preserves sharing between its imported tiles.

**22. Why reject unused insight definitions?**

Every imported definition should belong to the dashboard graph being imported.
Rejecting unused definitions avoids silently creating unrelated saved insights.

**23. Why reject actual cohort-targeted filters instead of searching JSON for
the word `cohort`?**

A parsed cohort filter carries a project-local dependency. An ordinary string
value containing that word does not. Validation should follow structure and
meaning, not an accidental substring.

**24. Why count body bytes when Content-Length is absent?**

The request can still be oversized. Reading at most the limit plus one byte
detects that boundary without consuming and parsing an unbounded document.

## Roles, identity, and audiences

**25. Why can an old membership become admin during migration while a new invitation starts as viewer?**

Migration preserves previously available access. Invitation establishes new
access using a deliberate default. Follow both paths in Session 8 and explain
which user would be surprised if those defaults were accidentally swapped.

**26. Why is a canonical person ID different from a distinct ID?**

Several device/account aliases can map to one person. Timeline queries follow
the canonical ID so merged identity history appears together. Count the person
once even when its alias table has several rows.

**27. Why does a dynamic cohort preview not need a stored cohort row?**

Rule evaluation is a reusable operation over a project and candidate rules.
Preview returns the evaluation without persisting a definition or memberships.
A snapshot deliberately turns the current evaluation into a static set.

**28. Why validate the entire replacement membership set before deleting old rows?**

One foreign or unknown person must reject the replacement as a whole. Validation
and the transaction preserve the previous valid set when the candidate fails.

## Diagnostics and observation

**29. Why must flag explanation call the same decision code as evaluation?**

Two independently maintained rule implementations can disagree. Shared decisions
return a value plus structured reasons; normal evaluation exposes only the value.
The diagnostic output does not require disclosing person properties.

**30. Why is a request-local cohort cache safer than a permanent dictionary?**

It reuses repeated work within one operation, then expires with that operation.
The next request sees changed cohort rules or memberships. Verify read reuse
and fresh subsequent results as separate assertions.

**31. Why can a replay batch succeed for its first item and fail later?**

Each item owns its transaction. The response reports expected per-item outcomes;
unexpected storage failure stops processing without undoing already committed
items. An outer all-or-nothing promise would be a different contract.

**32. Why hash the exact download bytes instead of the in-memory JSON object?**

Whitespace, encoding, and line endings change byte identity without changing
the parsed data. Download and integrity share UTF-8 conversion so a client can
hash the bytes it actually received and compare the same representation.

**33. Why does top-one event usage still need the total across all event names?**

The requested limit constrains displayed groups. It does not redefine the
denominator. Compute the full scoped aggregate before applying the top limit.

**34. Why is an oversized scan an error rather than an apparently valid partial chart?**

A partial chart can make omitted events look like absent events. A bounded
contract returns a clear failure and lets the caller narrow its range. Counts
must state the dataset they actually represent.

## Trustworthy changes

**35. Two clients saw revision 4. Why does checking `Revision == 4` in memory not settle the race?**

Both can pass that check before either saves. The actual SQL update must include
the expected revision. Only one write can change revision 4 to revision 5.

**36. Why is a 412 response a decision for the caller rather than an automatic retry instruction?**

Another edit changed the state the caller assumed. Retrying its stale body with
a fresh token can erase that edit. Read, compare, and reconcile the intended change.

**37. Why can an audit-save failure roll back an update that already executed?**

The SQL update ran inside an uncommitted outer transaction. That transaction
also contains audit/history persistence, so failure discards all of its changes.
Trace `UpdateInTransactionAsync` to its caller's commit.

**38. Why does restoring revision 2 from current revision 9 create revision 10?**

Restoration is a new change using old configuration. Reusing revision 2 would
make old concurrency tokens meaningful again and confuse the change history.

**39. Why might a schedule be blocked although creation returned 202?**

Permission can change between admission and execution. The creator's current
membership is checked when due. The schedule records blocked without editing
the flag if that permission no longer qualifies.

**40. Why can two processors select the same due schedule safely?**

Selection is only a candidate list. Inside a transaction each processor performs
a conditional pending-state write. After one commits a terminal state, the other
cannot claim that pending transition or advance the flag again.

## Transfer exercise

**41. A retry key expires while its event is queued. Is the processing obligation gone?**

No. Client retry keys, processing items, and receipt positions have separate
identities and lifetimes. Expiration stops deduplication against that client key;
it does not acknowledge or delete the queued work.

**42. Two identical positions share one admission. Why can the receipt report two processed positions?**

Receipt counts describe submitted positions. Both positions reference the same
processing item, so one committed event can satisfy both without duplicate storage.

**43. Why check capacity after deduplication?**

Capacity limits additional queued work. A retry of already accepted input adds
no queue row and should still succeed when the project has reached its limit.

**44. Why does a restart not reset transient retry timing?**

NextAttemptAt and Attempts live on the queue row. The next processor reads those
facts rather than reconstructing them from a lost timer or in-memory counter.

**45. What stops an expired ingestion worker from committing after reassignment?**

Every mutation transaction checks the current owner and generation. A check at
batch admission alone would leave later rows vulnerable to a stale owner.

**46. Why can cancellation lose a race to completion without being a bug?**

One contested transition commits first. Completion first means cancellation
returns 409; cancellation first means publication matches zero rows. The bug
would be acknowledging cancellation and then publishing that cancelled attempt.

**47. Is an empty snapshot unfinished?**

Only its ready marker can answer. A completed capture of zero selected rows is
valid; using row count as the marker would cause repeated recapture on recovery.

**48. Does a snapshot job's new retry keep its old capture time?**

A new retry job captures fresh input. Recovery of the same job reuses its ready
snapshot and capture time. Compare the job IDs before deciding which applies.

## Apply a card elsewhere

**49. An event is exactly at a retention cutoff. Is it deleted?**

No. Eligibility uses Timestamp < Cutoff. Equality stays. A batch retains its
recorded cutoff instead of moving it as the clock advances.

**50. Why does erasure freeze aliases before removing mappings?**

The mappings are evidence of which identities belong to the person. Once deleted,
they cannot reliably be reconstructed. Frozen keyed fingerprints let the system
remove known work and reject later recreation without retaining raw aliases.

**51. Why can a completed erasure still reject an old request's save?**

The pause ended, but its maintenance generation changed. A request authorized
under the previous generation cannot regain write authority merely by waiting.

**52. Why does an unreadable queue item prevent completion?**

The system cannot prove whether it refers to the erased person. A metadata-only
review records the uncertainty; explicit hash-checked selection authorizes discard.

**53. Does a restricted token with configuration:write also have configuration:read?**

No. Named scopes are explicit and independent. Current role, project allowance,
required scope, and expiry must all permit the operation.

**54. Why hide project credentials from a restricted Admin token?**

Otherwise the restricted token could retrieve a broader write/read credential
and bypass its intended limits. Serialization is part of the authorization boundary.

**55. One event has five attempts. How many span IDs should it have?**

Five distinct attempt span IDs, plus producer/request spans. The trace groups the
journey; a span identifies a particular execution step. The receipt has a different purpose.

**56. What happens when sampling collects no span?**

Business processing continues. The helper preserves safe local correlation;
collector availability never controls committing an event or acknowledging work.

**57. Why charge scanned bytes before evaluating a selective filter?**

The provider already read those values. A filter that matches one row does not
erase the cost of inspecting thousands of nonmatching rows.

**58. Why read limit plus one?**

The extra row proves overflow. Reading exactly the limit cannot distinguish a
complete result from silent truncation of a larger result.

**59. Does a two-second deadline guarantee a response within two seconds?**

It requests cancellation at that time. Provider cleanup and scheduling can take
longer. Client cancellation remains distinct from the server deadline.

**60. Events occur at 10:00, 10:10, 10:40 with a 30-minute gap. How many sessions?**

Two. Equality starts a new session. Counts are 2 and 1; observed durations are
600 and 0 seconds. Neither duration proves total time on site.

**61. Why can a session's ordinal change tomorrow?**

Sessions are derived from current canonical identity and processed events within
the chosen window. A merge or late arrival can change grouping without editing session rows.

**62. An alert is edited at 10:15. Which window is first eligible?**

11:00–12:00, evaluated after noon. The 10:00–11:00 window was already partly
elapsed when the revision became active. An edit exactly at 10:00 may use 10:00–11:00.

**63. Why commit an alert evaluation and notification together?**

A crash between separate commits could record evaluation without delivery or
produce duplicate delivery on retry. One transaction includes durable progress too.

**64. Is one user's read marker part of the notification itself?**

No. The immutable notification is shared project information; the read marker
belongs to one authenticated user. Another reader's inbox remains independently unread.

## Transfer these ideas to another feature

Choose one card and apply its principle to another feature. For example, use
the check-then-write idea to explain preserving the last project administrator,
or use document-local references to explain why a copied configuration must
not copy credentials. The ledger tracks those features; distinguish a
design prediction from behavior already verified in this checkout.
