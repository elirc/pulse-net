# Learn backend engineering with Pulse

Want a slower, more visual introduction to this particular codebase first?
Use [AstraDocs](../../astradocs/README.md), which revisits one event through
stories, diagrams, code excerpts, worked examples, and recall exercises.

Your goal is to become someone who can explain, change, test, and operate a
backend independently. Pulse is your practice system. A completed feature is
useful evidence only when you can explain why it works and where it fails.

Start with [your first session](01-first-session.md). You do not need to
understand the whole repository before changing one small behavior.

For story-by-story navigation, use the bootcamp's human-readable
[story map](../../astradocs/bootcamp/story-map.md). It links each of the 75 user
stories to its original plan, lesson, implementation entry points, and current
ledger status. Start with the first session for a guided request; start with the
story map when you already know the behavior or skill you want to study.

## Choose your starting point

| If you can currently... | Start here | Produce this evidence |
| --- | --- | --- |
| Read a little C# but cannot follow a request | [First session](01-first-session.md), [C# foundations](02-csharp-foundations.md) | Run one test; draw one request path |
| Implement a happy path with help | [HTTP and authorization](03-http-and-authorization.md), [data and transactions](04-data-and-transactions.md) | A feature with invalid-input and cross-project tests |
| Deliver a bounded feature independently | [Async reliability](05-async-reliability.md), [testing and debugging](06-testing-and-debugging.md) | A reproduced failure and a regression test |
| Deliver reliably but struggle with broader decisions | [Performance](07-performance.md), [design and operations](08-design-and-operations.md) | Measurements, alternatives, rollout and recovery plan |

## The working loop

1. **Predict:** write what should happen before running the example.
2. **Trace:** name the endpoint, service, database writes, and observable result.
3. **Change:** make the smallest change that answers the exercise.
4. **Prove:** test success, failure, boundaries, and project isolation as appropriate.
5. **Explain:** write a short review note with the tradeoff and remaining limitation.
6. **Repeat from memory:** revisit the exercise another day without copying the solution.

Use the [roadmap](roadmap.md) to sequence work and the
[competency rubric](competency-rubric.md) to assess independence. There is no
promise that a fixed number of weeks earns a job title; senior scope includes
team judgment and ownership that a solo repository can only simulate.

## Practice material

| Resource | Purpose |
| --- | --- |
| [Code review findings](review-findings.md) | Real strengths, fixed defects, and remaining limitations |
| [Validation record](validation.md) | Passing checks, an unresolved batch timeout, and the Windows runtime blocker |
| [Worked feature: ingestion recovery](worked-example-ingestion.md) | Follow requirements through implementation and failure tests |
| [Exercise backlog](exercise-backlog.md) | 20 progressively harder tasks with acceptance criteria |
| [Debugging labs](debugging-labs.md) | Diagnose deliberately introduced failures in isolated tests |
| [Review and learning templates](templates.md) | Record predictions, evidence, design choices, and feedback |
| [Glossary](glossary.md) | Plain-language terms linked to the code |
| [Operations runbook](../runbooks/ingestion.md) | Practice investigating a queue incident |

## Getting useful help

Ask a reviewer or assistant: "Here is my prediction, the test, the actual
result, and my hypothesis. Give me one hint before a solution." After a
solution, explain it yourself and change one requirement without help.
If you cannot explain a generated method, treat it as material to study,
not evidence that you have mastered the skill.

Keep answers in your own notes using [the learning journal](templates.md).
Commit small, coherent changes. Review your diff before asking for review.

The [API reference](../api-reference.md), [architecture](../architecture.md),
and [ADRs](../adr/README.md) are reference material. Read the parts your
current exercise touches; you do not need to memorize every route.
