# pulse-net — Executive Summary

**Audience:** CEO / executive team
**Date:** 2026-09-09
**Prepared from:** a full independent review — the code was built, the test suite
was executed, and the documentation was read in full. Nothing below is taken on
the project's own word.

---

## The one-paragraph version

pulse-net is a **product-analytics platform** — the category occupied by PostHog
and Mixpanel — built to a high engineering standard. The functional core is
complete and demonstrably works: 110 API endpoints covering event ingestion,
identity resolution, trends, funnels, retention, cohorts, feature flags,
dashboards, exports, data-privacy compliance and alerting, backed by 656
automated tests that all pass. **It is also entirely invisible to a customer:
there is no user interface, no client libraries, and no way to deploy it.** It is
an engine without a car around it.

Two things need a decision from you. One is urgent and takes an hour. The other
is strategic and defines the next year.

---

## 1. Urgent: most of the work is not backed up

**Roughly two thirds of the software exists only on one person's laptop.**

The project's shared repository — the copy that survives if a machine dies —
contains 102 source files. There are 164 more that have never been saved to it,
along with 83 of the 97 documentation files. That unsaved portion includes the
entire second phase of the project: the permissions system, the database upgrade
machinery, data-privacy and retention features, reliability work, alerting, and
substantially all of the documentation.

No branch. No backup. No copy anywhere else.

A failed hard drive, a stolen laptop, or a routine cleanup command deletes months
of finished, tested work. There is nothing technically wrong — the code builds
cleanly and passes every test. It has simply never been checked in.

**Ask for this to be committed and pushed today.** It is a one-hour task with an
outsized downside if it waits.

---

## 2. Quality: unusually good, and I verified it

I did not rely on the team's own reporting. I compiled the software and ran its
tests myself:

- **Builds with zero errors and zero warnings** across ~26,000 lines of code.
- **656 automated tests pass; none fail, none are skipped.**
- Test code is nearly equal in volume to application code — a ratio associated
  with software that behaves predictably under change.
- **Zero "TODO" or "fix this later" markers** anywhere in the codebase. In my
  experience reviewing code, this is rare and means work was finished rather than
  abandoned mid-stream.
- Only **four** external software dependencies in the entire application. Most
  comparable systems carry dozens to hundreds. This materially reduces
  security-vulnerability and licensing exposure.

More telling than the numbers: the tests deliberately simulate crashes, retries,
and restarts at the worst possible moments, and verify the system recovers
correctly. That is the kind of testing that prevents 3 a.m. incidents, and most
teams skip it.

The team also maintains its own written list of the system's weaknesses and
distinguishes clearly between "we proved this works" and "we know this is a
limitation we chose to accept." I spot-checked those claims against the code and
found them accurate. **This is the strongest signal in the review**: when a team
documents its own shortcomings this precisely, its claims of completion can be
trusted.

**One caveat:** all development happened in a very short window, and every commit
carries the same date. The output is real and verified, but you cannot use this
history to forecast how fast the team will deliver the next thing.

---

## 3. What it would take to have a customer

The functional core is done. The commercial packaging is not started.

| Missing | Effort | Consequence if skipped |
| --- | --- | --- |
| **A user interface** | Large — a full project | No customer can use any feature. Everything is invisible. |
| **Client libraries** | Medium | Customers must hand-write technical plumbing to send data. Blocks adoption. |
| **Deployment capability** | Small | The software currently runs on a developer's machine and nowhere else. |
| **Email** | Small | Team invitations don't work for new people. Alerts have nowhere to go. |
| **Security hardening** | Small | Two specific, fixable issues: a development password is stored in the code, and a usage limit can be circumvented. Hours of work each. |
| **Scale re-platforming** | Medium | The database is a single file suited to one customer at a time. Fine for a pilot; not for a business. |

The security items are small and specific — this is not a systemic weakness, and
the underlying security design (how passwords and access keys are stored, how
permissions are checked) is done correctly and tested.

---

## 4. The strategic question

The README leads not with the product but with a **training curriculum**. Sitting
alongside the code are 97 documents totaling roughly 124,000 words — about the
length of a technical book — comprising 25 structured lessons, architecture
decision records, operational runbooks, a skills rubric, and a learning journal.
The project states in multiple places that *"learning content is the primary
deliverable."*

That explains the shape of what I found: exceptional documentation, rigorous
failure testing, and honest self-assessment — alongside no interface, no client
libraries, and no deployment path. Those are exactly the priorities you'd expect
if the goal were to *teach* how to build this kind of system rather than to sell
one.

**So the question is: which is it?**

**If this is a training asset**, it is a strong one and it is essentially
complete. Commit the work, put the documentation somewhere people can read it,
and the investment is realized. Further product engineering adds little.

**If this is a product**, then you own a well-built engine and roughly a year of
remaining work to make it sellable — most of it in areas (interface design,
client libraries, deployment, billing) that are different disciplines from what
has been built so far. The good news is that the hard, invisible part — the part
that is expensive to retrofit and painful to get wrong — is finished and proven.

**A middle path exists.** The feature-flag capability is the most complete and
most self-contained part of the system: versioning, scheduled rollouts,
targeting, and diagnostics are all done. It is small enough to wrap in an
interface and ship in a quarter rather than a year, which would test market
appetite without committing to the full analytics build.

---

## 5. What I'd ask for

| When | Action | Effort |
| --- | --- | --- |
| **Today** | Commit and push the unsaved work | 1 hour |
| **This week** | Turn on automated build-and-test checks; fix the two security items | 1 day |
| **This month** | Decide: training asset, full product, or feature-flags-first | A conversation |
| **Then** | Resource according to that decision | Depends entirely on the above |

The first two rows are unambiguous and cheap; I'd authorize them regardless of
where the strategic question lands. The third is the one that only you can
answer, and everything downstream depends on it.

---

## Bottom line

The engineering is better than most of what I review — verified, honest, and
finished. The risk is not technical quality; it is that the majority of that work
currently exists in exactly one place and could be lost this week. Fix that
immediately, then decide what the asset is for.
