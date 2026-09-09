# Templates for deliberate practice

Copy the relevant template into your own notes or PR description. Replace
prompts with evidence. A filled template is useful only if its claims are true.

## Learning journal

```markdown
Date / exercise:
What I can explain before starting:
Prediction:
Code path and test I will inspect:
Observed result:
My hypothesis and how I tested it:
Change made:
Evidence (command, test, measurement, or diff):
What I still cannot explain:
One related task to repeat without help:
```

## Feature specification

```markdown
Caller and problem:
Input and output examples:
Authorization and project boundary:
Success, invalid input, missing resource, repeated request:
Invariant and transaction boundary:
Cancellation / concurrency / restart behavior:
Compatibility and schema effects:
Acceptance tests:
Operational visibility and recovery:
Explicit non-goals:
```

## Review-ready change

```markdown
Problem and resulting behavior:
Concrete before/after example:
Why this approach:
Validation performed and results:
Failure case the tests establish:
Known limitation and rollout/recovery considerations:
One question where reviewer judgment would help:
```

## Design decision

```markdown
Title and status:
Context and required behavior:
Option A / benefits / costs:
Option B / benefits / costs:
Decision and rationale:
Failure model and consistency guarantee:
Migration and operational consequences:
Evidence we have / assumptions still untested:
What would cause us to revisit this:
```

## Incident exercise

```markdown
User-visible symptom and scope:
Timeline (observations, not guesses):
Evidence and ruled-out hypotheses:
Immediate recovery action and why it is appropriate:
Verification of recovery:
Contributing conditions:
Prevention or detection improvement with an owner:
```
