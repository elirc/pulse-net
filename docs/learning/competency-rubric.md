# Assess skills with evidence

This is a practice rubric, not an employer's leveling standard. Assess a
recent change you actually made; link evidence in your learning journal.

| Skill | Junior foundation | Mid-level independence | Senior scope to practice |
| --- | --- | --- | --- |
| Code comprehension | Trace a method and explain its inputs | Trace a feature across layers | Identify architectural constraints and misleading assumptions |
| Implementation | Complete a bounded change with guidance | Deliver a feature from a contract | Break ambiguous work into safe, coordinated increments |
| Correctness | Test expected output and one boundary | Cover failures, isolation, and compatibility | Define invariants across concurrency, restart, and migration |
| Debugging | Reproduce and locate a failure with help | Form and test hypotheses independently | Guide incident investigation and improve future diagnosis |
| Data | Understand entities and queries | Design constraints and transaction boundaries | Plan data evolution, recovery, and growth |
| Performance | Identify obvious excess work | Measure and fix a bottleneck | Establish capacity assumptions and system-wide tradeoffs |
| Security | Follow existing auth patterns | Test permissions and tenant boundaries | Review a feature's abuse model and operational exposure |
| Communication | Explain what changed | Explain why, evidence, and limitations | Align others on requirements and resolve competing constraints |
| Ownership | Run the documented checks | Document and support what you build | Plan rollout, monitor outcomes, and mentor others |

## Self-review

For each row use: **not yet**, **with help**, **independent**, or **can explain
and review someone else's work**. Avoid a single averaged score; strong C#
syntax cannot compensate for missing authorization checks.

Choose one weak row, select an [exercise](exercise-backlog.md), and write
what evidence would change your assessment. Ask a reviewer to assess the
same evidence. Discuss disagreements using concrete examples.

## Questions that reveal understanding

1. Why can the queue be empty after an unsuccessful capture?
2. Where could a crash previously produce duplicate events?
3. Why do transactions not deduplicate repeated HTTP requests?
4. Why is an authorized project ID insufficient to safely find a child by ID?
5. What happens when an old `$set` event is replayed after a newer one?
6. Which existing behavior limits running multiple workers?
7. How would you prove a query became faster without changing its answer?
8. Which change would require a schema migration and how would you deploy it?

A strong answer names code, a failure scenario, and a way to verify the
claim. "Best practice" without a reason or tradeoff is incomplete.
