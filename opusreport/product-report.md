# pulse-net — Product & Delivery Report

**Audience:** product management
**Date:** 2026-09-09
**Basis:** full code review, executed build and test run, complete documentation read

---

## 1. What this product is today

pulse-net is a **product-analytics backend** in the PostHog / Mixpanel mold:
customers send behavioral events from their apps, the platform resolves those
events into people, and it answers questions about what those people did.

It is **API-only**. There is no user interface, no JavaScript SDK, no mobile SDK.
Every capability below is exposed as an HTTP endpoint. Anything a customer would
actually *look at* would have to be built on top.

The feature surface is genuinely broad — **110 distinct API routes** — and the
functional core is complete and tested end to end.

### Capability inventory

| Area | State | Notes |
| --- | --- | --- |
| **Event ingestion** | Complete | Single + batch (max 1000), async with durable queue, returns 202, per-key rate limiting |
| **Identity resolution** | Complete | Anonymous → known person merging via `$identify`, PostHog-compatible `$set` / `$set_once` semantics |
| **Trends** | Complete | Time-bucketed counts + unique persons, property breakdowns with top-N + "(other)", dated annotations inline |
| **Funnels** | Complete | Ordered per-person step conversion within a configurable window |
| **Retention** | Complete | Day-N cohort retention triangle |
| **Cohorts** | Complete | Static (explicit membership) and dynamic (property + behavioral rules) |
| **Feature flags** | Complete | Boolean + multivariate, deterministic percentage rollout, targeting filters, `/decide` endpoint, SDK local-evaluation payload |
| **Flag governance** | Complete | Version history, restore, clone, scheduled rollouts, explain/diagnostics |
| **Dashboards** | Complete | Tiles from saved insights, one-shot refresh, selective refresh, duplication, templates, export/import |
| **Data export** | Complete | Sync cursor-paginated CSV/JSON, plus async jobs with resume, retry, cancel, integrity check |
| **Data governance** | Complete | Retention policies, GDPR-style person erasure, audit log, restricted scoped tokens |
| **Alerting** | Complete | Hourly threshold rules with in-app notifications and read state |
| **Team management** | Partial | Projects, roles (Admin/Editor/Viewer), membership — but invites only work for users who already have an account |

### Not built

- **No web UI of any kind.** This is the largest gap between "what exists" and
  "what a customer could use."
- **No client SDKs.** Customers would hand-roll HTTP calls.
- **No email.** No invitations, no password reset, no alert delivery — alerts
  land in an in-app notification API that has no app.
- **No SSO, no billing, no usage metering.**
- **No session replay, heatmaps, surveys, or A/B test statistics** — the
  adjacent features competitors bundle.

---

## 2. Delivery state — the thing to act on this week

**Two thirds of the codebase is not in version control.**

The repository's shared history (`origin/main`) ends at commit `c628672`, which
is the end of the original 17-sprint build. Everything after that — a 75-story
program covering roles and permissions, schema migrations, flag governance,
ingestion reliability, exports, retention, erasure, alerts, and roughly 124,000
words of documentation — exists **only as uncommitted files on one machine**.

| | In shared history | Local only |
| --- | --- | --- |
| C# source files | 102 | **164** |
| Markdown docs | 14 | **83** |

There is no branch and no backup. A hardware failure, a laptop replacement, or a
routine cleanup command loses all of it. Nobody else can see it, review it,
build on it, or continue it.

**Recommended action: get this committed and pushed today.** It is not a
technical problem — the code builds clean and passes every test. It is purely a
matter of someone running the commands.

Related: the project's verification evidence (test result files, upgrade
rehearsal records) is stored in a directory that version control is configured to
ignore, so the proof behind "all 75 stories verified" is also single-copy and
local.

---

## 3. Quality signal

I built and ran everything myself rather than trusting the documentation:

- **Build: clean.** Zero errors, zero warnings across ~26,000 lines.
- **Tests: 656 passed, 0 failed, 0 skipped**, in 5 minutes.
- **Test-to-code ratio is high** — 13,094 lines of tests against 26,263 lines of
  source (about half of which is generated database-migration scaffolding, so the
  effective ratio is close to 1:1).
- **Zero TODO/FIXME/HACK markers** in the entire codebase. In my experience this
  is rare and indicates work was finished rather than parked.

Crucially, the tests are **not** shallow unit tests. Most spin up the real
application with its background workers running against a real (in-memory) SQL
database. They test crash recovery, transaction rollback, retry behavior, and
worker restart — the failure modes that usually surface in production instead.

The project also maintains its own honest limitations list
([`docs/learning/review-findings.md`](../docs/learning/review-findings.md)) that
distinguishes "implemented and proven" from "known limitation we chose to leave."
I verified several of those entries against the code and found them accurate. A
team that documents its own weaknesses this precisely is a team whose "done"
claims you can generally trust.

**Caveat on velocity:** all 35 commits are dated the same day, and the 75-story
program was completed in a single working session. The output is real and
verified, but this is not a track record you can extrapolate a sprint velocity
from.

---

## 4. What blocks this from being usable by a real customer

Ranked by what actually stands between here and a first pilot user.

### Tier 1 — nothing works for an end user without these

1. **A user interface.** Every feature is an API. A customer cannot see a trend,
   build a dashboard, or toggle a flag without one. This is a full frontend
   project, not a task.
2. **At least one client SDK.** Asking customers to hand-write batched HTTP
   ingestion with retry semantics is a non-starter for adoption.
3. **Deployability.** There is no container image, no deployment configuration,
   and no automated build pipeline. Today the software runs on a developer's
   machine and nowhere else.

### Tier 2 — needed before anyone outside the team touches it

4. **Email.** Team invitations only work if the invitee already has an account,
   which nobody will. Alerts have no delivery channel.
5. **A development secret is committed to the repository** that would let anyone
   with the code impersonate any user on a deployment that forgot to override it.
   Small fix, serious consequence.
6. **A rate-limiting flaw** lets a client sidestep the ingestion limit by sending
   its key in a different part of the request. Small fix.
7. **An unauthenticated endpoint** exposes platform-wide queue statistics to
   anyone who can reach the server. Needs a deliberate decision, not necessarily
   a change.

### Tier 3 — capacity ceiling, arrives with the second real customer

8. **The database is SQLite** — a single file with a single writer. Fine for a
   pilot, not for concurrent tenants.
9. **Read requests currently serialize.** Because of how the system guarantees
   correctness during data-erasure operations, dashboard queries execute one at a
   time regardless of how much hardware you give it. Ten users refreshing
   dashboards will queue behind each other. This is understood and documented by
   the team; it has not been measured.
10. **The original query engine loads data into memory** to compute results. Wide
    date ranges on a large customer will strain it. A newer, bounded query path
    exists and rejects oversized work — but the dashboards still use the old one.

---

## 5. How I'd frame the roadmap

**Phase 0 — this week (hours, not days)**
Commit and push. Add an automated build-and-test pipeline. Fix the committed
secret and the rate-limit key. Correct the one stale instruction in the README
that tells new developers to delete their database (it describes behavior that
was replaced by the migration system).

**Phase 1 — make it deployable (small)**
Container image, deployment configuration, HTTPS/CORS setup, a health and metrics
story. This is the difference between "it builds" and "it runs somewhere."

**Phase 2 — make it usable (large)**
This is the real decision point, and it is a scoping question rather than a
technical one. Three viable shapes:

| Direction | Build | Best when |
| --- | --- | --- |
| **Embedded analytics backend** | Ship SDKs + polish the API + generate API docs. No UI. | The customer is another engineering team embedding analytics into their own product. Read keys and the local-evaluation flag payload already support this well. |
| **Full analytics product** | Everything above, plus a web app, plus email, plus billing. | You're competing with PostHog directly. Much larger investment; the backend is genuinely ready for it. |
| **Feature-flag service first** | Ship a small flag UI + SDKs. Flags are the most complete, most self-contained area (versioning, scheduling, targeting, deterministic rollout, diagnostics all done). | You want something shippable in a quarter rather than a year. |

**Phase 3 — scale**
Move off SQLite, resolve the read-serialization constraint, migrate dashboards to
the bounded query engine. Nothing here is urgent until real usage exists, and the
architecture is structured so these are replacements rather than rewrites.

---

## 6. One unusual thing worth understanding

The repository states in several places that **"learning content is the primary
deliverable"** — the README leads with a bootcamp, not with the product.
Alongside the code sit 97 documents totaling ~124,000 words: 25 structured
lessons, 64 review cards, 10 architecture decision records, 8 operational
runbooks, a competency rubric, debugging labs, and an implementation journal.

That reframes what you're looking at. This is a working analytics platform that
was built primarily as a teaching artifact — which explains both its unusual
strengths (rigorous documentation, honest limitation tracking, deep failure-mode
testing) and its unusual gaps (no UI, no SDKs, no deployment story, no CI).

Before planning a roadmap, it's worth confirming with the owner which of these is
the actual goal: a product to ship, or a curriculum to teach from. The answer
changes essentially every priority in §5, and the two are not in conflict — but
they are different projects.
