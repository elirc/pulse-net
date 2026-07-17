# ADR 0003: Flag rollout is a deterministic SHA-256 hash, not stored assignments

**Status:** accepted

## Context

A percentage rollout needs to decide, per user, whether a flag is on. Storing
an assignment row per (flag, user) makes every `/decide` a write, grows a
table with the product of flags × users, and needs backfill logic whenever a
rollout percentage changes. Random sampling per request is stateless but
gives users a different answer on every call — unacceptable for UX and for
experiment integrity.

## Decision

`FeatureFlagHasher.Rank` computes `SHA-256("{flagKey}.{distinctId}")`, takes
the first 8 bytes big-endian, and divides by `ulong.MaxValue` to get a stable
position in `[0, 1]`. A flag at N% is on when `rank * 100 < N` (strict
less-than; 100% short-circuits). Variant selection re-ranks with a
`".variant"` salt so the A/B split is statistically independent of the
rollout gate, walking cumulative weights that must sum to 100.

## Consequences

- Zero storage and zero writes: evaluation is pure math over data already in
  the flag row. `/decide` and SDK local evaluation (which receives the full
  definitions) compute identical answers.
- Deterministic and **monotonic**: the same user always gets the same answer,
  and raising a rollout only ever adds users — nobody flips off during a
  ramp-up. Both properties are pinned by tests, including a golden-value test
  of the exact hash recipe, because changing the recipe would silently
  reshuffle every live rollout.
- Bucketing is per-flag (the key is part of the hash input), so being in the
  10% for one flag says nothing about another flag — no correlated cohorts.
- No per-user overrides ("force this user on") without adding a separate
  targeting mechanism; property/cohort filters cover the common cases.
- Distribution quality rests on SHA-256's uniformity, which is more than
  sufficient; it is intentionally *not* a keyed/secret hash — predictability
  by someone who knows the flag key is not in the threat model.
