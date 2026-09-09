# Ten short sessions, with deliberate revisits

Use one row per sitting. A sitting can be ten minutes or longer; pause when
you need to. The order gives you repeated contact with the same code from
different directions, rather than ten unrelated topics to memorize.

| Session | First recall | New view or action | Leave with |
| --- | --- | --- | --- |
| 1 | What might an analytics backend do? | [First 20 minutes](01-first-20-minutes.md) | One sentence about Pulse |
| 2 | Say that sentence without looking | [Big picture](02-big-picture.md), then [visual tour](index.html) | A five-box sketch |
| 3 | Rebuild the sketch | [Repository map](03-repository-map.md), [event story](04-one-event-story.md) | One filename per box |
| 4 | Explain capture versus processing | [Data shapes](05-data-shapes.md) | Distinguish request, queue row, event, answer |
| 5 | Trace `device-7` aloud | [Code close-up](06-code-close-up.md) | Explain Add, SaveChanges, Ring, Accepted |
| 6 | Who supplies CaptureService? | [Startup](07-startup-and-wiring.md), [people/tables](08-people-and-tables.md) | Explain scope, User, Person, distinct ID |
| 7 | Name User versus Person again | [Keys](09-keys-and-permissions.md), cards 1–6 | Explain one access denial |
| 8 | Why can an empty queue hide failure? | [Feature tour](10-feature-tour.md), [tests](11-tests-as-examples.md) | Match three features to their questions |
| 9 | Read one existing assertion aloud | [First change](12-first-change.md), cards 7–12 | A small diff and an honest result note |
| 10 | Retell the journey using a purchase | [Mentor conversation](14-mentor-conversation.md), cards 13–18 | Explain one new scenario with less help |

## Revisit after a break

At your next sitting, draw the capture path before reopening a guide. Mark
the box you forgot. Revisit just that section, then explain it differently:
use the story if the diagram was unclear, or real rows if the story felt abstract.

These are practical study suggestions, not a required pace or a guarantee
of mastering a topic after a particular interval.

## A tiny progress note

Copy this into personal notes:

```text
Today I traced:
I predicted:
I observed or found in source:
I can explain:
I needed a hint about:
Next time I will revisit:
```

"I found where membership is checked" is useful progress. "Read everything"
does not tell you which part you can explain independently.

## For a mentor or pair-programming partner

Ask one concrete question and allow time to think. Ask the learner to point
to evidence in code. Offer a small hint before a full solution when they
want that help. Reuse the same example until the relationships are clear,
then change one detail to check transfer.

Avoid quizzing the learner on several unexplained terms at once. Review the
reasoning they used, including a good hypothesis that turned out to be wrong.

When these sessions feel familiar, choose one task from the existing
[junior-to-senior roadmap](../docs/learning/roadmap.md).
