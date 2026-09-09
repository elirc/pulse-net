# Session 11: explain a decision without changing it

Stories: MID-16 through MID-19. The [ledger](stories.json) records pending tests
separately from verified behavior. This lesson follows the implementation and
explains what each acceptance test is intended to establish.

## Start with the product question

“Why does Alice see the old navigation?” is not answered by “the flag is active.”
An active flag still has targeting and rollout gates. A multivariate flag has
another selection step after it is on. The explanation endpoint shows those
stages without returning the person's private properties.

| Stage | Question | Possible continuation |
| --- | --- | --- |
| active | Is this flag enabled for evaluation? | If inactive, stop with false |
| targeting | Does this identity satisfy the audience rules? | If a rule fails, stop with false |
| rollout | Does the deterministic hash fall inside the rollout? | If outside, stop with false |
| variant | Which variant does the hash select? | Boolean flags skip variant selection |

After an early failure, later stages are **skipped**, not failed. A skipped
rollout was never consulted. That difference matters when explaining why a
decision occurred and when deciding what configuration change would help.

Think of entering a venue: first it must be open, then you need an eligible
ticket, then you may be assigned a section. If the venue is closed, claiming
your section assignment failed would tell the wrong story.

## One path produces both values and explanations

Read [FeatureFlagService](../../src/Pulse.Infrastructure/Services/FeatureFlagService.cs).
`EvaluateDecisionAsync` produces a structured `FlagDecision`. `/decide` returns
only its value, while the management explanation returns its stages and warning
codes too. There is one execution path for targeting, rollout, and variants.

This is a refactor with a behavioral obligation. If a separate explanation
algorithm were copied into another service, a later bug fix could update only
one copy. The explanation might then sound plausible while disagreeing with the
actual SDK result. The compatibility test compares every explanation value
against `/decide` for the same fixture and identity.

The endpoint is in
[FlagDiagnosticEndpoints](../../src/Pulse.Api/Endpoints/FlagDiagnosticEndpoints.cs).
It requires project viewer access. The SDK's project write key still calls
`/decide`; it does not gain access to this management diagnostic response.
Identity input is trimmed and limited to 1–400 characters.

## A warning describes an inherited fallback

Legacy malformed targeting is currently treated as unrestricted. Legacy invalid
or empty multivariate variants can fall back to boolean-on after earlier gates
pass. Explanations preserve those results and attach stable warning codes:
`invalid-targeting-unrestricted` and `invalid-variants-boolean-on`.

Do not confuse describing that fallback with recommending malformed
configuration. New draft validation can reject an invalid source while the
existing evaluator retains compatibility for a stored legacy record. Changing
fallback policy would be another behavior change requiring explicit migration
and regression reasoning.

An explanation gives a reason such as `property-miss` or `cohort-miss`. It does
not echo the failed property's actual value or an entire cohort member list.
The tests use a distinctive private marker in a person's properties and assert
that it is absent from serialized explanations. A diagnostic feature should
answer the debugging question without copying unrelated personal data.

## Batch preview: reuse reads without inventing a lasting cache

The batch request names 1–50 unique trimmed identities and 1–20 unique keys.
The service loads those project flags once and validates the complete selected
key set before evaluating any pair. It loads identity mappings together, then
the required person properties together. Unknown identities are valid and do
not create a person record.

For three identities and two flags, there are six decisions. If both flags
reference the same cohort, six identical membership reads would be wasteful.
A dictionary local to this evaluation stores that cohort's member set after
the first read. The subsequent decisions reuse it. All database calls remain
sequential on the same scoped context.

The dictionary's lifetime ends with the call. It is neither static nor a
singleton service cache. The next request reads current membership again, so
updating a cohort becomes visible without an invalidation mechanism.

[FlagReadReuseTests](../../tests/Pulse.Tests/Infrastructure/FlagReadReuseTests.cs)
records the relevant SQL reads. It checks the scaling behavior for flag,
mapping, person, and shared-cohort reads, then removes membership and evaluates
again through the same service instance. The second answer must reflect the
new state. Counting only total HTTP requests would not prove the database reuse.

Aliases may share person properties while still receiving different rollout
results. The hash input is the requested distinct ID plus flag key. Reusing a
person lookup must not accidentally replace both aliases with the same hash input.

## Cloning creates an inactive experiment

Cloning copies type, percentages, targeting, and variants to a new flag ID and
key. It explicitly sets `Active=false`, gives it the requested name, and takes
a new creation timestamp. Even if a caller includes an extra `active` field,
the clone request does not use it. Activation is a separate deliberate edit.

[FlagConfigurationValidator](../../src/Pulse.Infrastructure/Services/FlagConfigurationValidator.cs)
shares configuration semantics with creation/editing. Its stored-source check
also requires actual array JSON and supported flag type; an invalid source
produces a conflict rather than a broken draft.

The [draft service](../../src/Pulse.Infrastructure/Services/FlagDraftService.cs)
does a friendly duplicate-key lookup, but that lookup alone cannot prevent a
race. Two requests can both see “absent” before either inserts. The unique
project/key index is the final authority. Only the matching SQLite unique-index
violation maps to 409; a general storage failure must remain a storage failure.

[FlagCloneConcurrencyTests](../../tests/Pulse.Tests/Infrastructure/FlagCloneConcurrencyTests.cs)
coordinates two saves after both prechecks. At most one new target row can
persist. That is a stronger test than calling clone twice sequentially.

The new key is also a hash input. Copying a 50% rollout to another key does not
promise the same people land in that 50%. This is why “same percentages” and
“same assignments” are different statements.

## Read-key rotation is a conditional write

`POST /api/projects/P/read-key/rotate` requires admin access and the expected
current read key in its body. The update predicate contains both project ID
and that expected value. If another request already changed the key, the update
matches zero rows and returns 409. A generated candidate is returned only after
its update succeeds, with `Cache-Control: no-store`.

This repeats the race lesson in a smaller form. Checking the key in memory and
then issuing an unconditional update would let a stale request overwrite the
winner. Putting the expectation in the database statement makes the condition
part of the mutation itself.

Only the read key changes. Project write-key capture and user login tokens
follow separate authentication paths. The old read key fails subsequent checks;
a request already authorized before rotation can finish. There is no grace
period or promise to recall bytes already sent to a client.

## Practice ladder

1. For an inactive flag, write four stage outcomes before opening the test.
   Explain the difference between failed and skipped.
2. Find where `/decide` discards the explanation metadata. Prove it still uses
   the same evaluator as the diagnostic route.
3. List what a useful property-miss response needs and what private information
   it can omit. Check the distinctive-marker test.
4. Draw six identity/flag pairs sharing one cohort. Circle the read that can be
   reused, then mark where the cache stops existing.
5. Explain why two aliases can share a person lookup but still hash differently.
6. Sketch two clone prechecks that both pass. Identify the database constraint
   that decides which save wins.
7. Read the rotation UPDATE predicate and explain why a second stale request
   cannot silently replace the first request's new key.

Teach back using ordinary language first: describe the gates, the temporary
reuse of reads, and the requirement that a value still match before changing
it. Then name the mechanisms: staged evaluation, request-local caching, and a
conditional mutation.
