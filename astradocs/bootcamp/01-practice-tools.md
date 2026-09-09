# Session 1 — Change the experiment without changing its meaning

Stories: US-01–US-06. Open [the demo](../../scripts/learning-demo.ps1) and
[the visual walkthrough](../index.html). Implementation and verification
status lives in [the ledger](stories.json); this lesson explains the design.
Run `node scripts/verify-walkthrough.mjs` from the repository root to repeat
the walkthrough's 12 browser checks with an isolated disposable browser profile.

## Start with a prediction

One visitor opens five pages. How many events and how many people should the
matching daily trend show? Write your answer before running the experiment.

The expected values are five events and one unique person in the matching
bucket. An event counts an occurrence; a person represents the identity behind
one or more occurrences. The script deliberately keeps `distinct_id` and the
timestamp the same while changing the number of items in `batch`.

```powershell
./scripts/learning-demo.ps1 -EventName signup -EventCount 5
./scripts/learning-demo.ps1 -EventName 'checkout & pay' -EventCount 1
```

These commands require a running local API. They create fresh practice accounts
and projects. Start with a disposable local database; the script retains its
practice data so you can explore it. It does not export the generated token.

## Read the script as four small responsibilities

**Input:** the parameter block supplies defaults and rejects counts outside
1–10. The name is trimmed and must contain 1–200 characters. This check runs
before the first HTTP request, so a blank name does not create a partial
practice account before failing.

**Construction:** the loop creates the event objects. `@(...)` deliberately
keeps the result an array when there is only one item. This matters because
the API expects `batch` to be an array. Similar mistakes occur in any language
when a caller accidentally changes a collection contract into a scalar.

**Transport:** the same normalized name enters both the JSON event and the
trend query. The query value is URL-encoded with EscapeDataString. The name
`checkout & pay` contains an ampersand, which otherwise separates URL query
parameters. Encoding preserves the name's meaning in that transport syntax.
Do not pre-encode the JSON event name: JSON has its own escaping rules.

**Evidence:** the script checks accepted count, then pending and dead-letter
state, then the actual trend. Acceptance alone proves neither processing nor
query correctness. A queue can empty because work failed. A successful query
can still use the wrong event name or time window. Together, the checks connect
the stages of the experiment.

## Say it another way

Imagine a delivery desk. The script submits five packages with the same sender.
A receipt says the desk accepted five packages. An empty waiting room says
none is still waiting. It does not tell you whether all five arrived. Checking
delivery outcomes is the last step. The number of packages is not the number
of senders.

Now replace those nouns with code: packages are event envelopes, the waiting
room is QueuedEvents, successful deliveries are AnalyticsEvents, and the sender
identity resolves to a Person. The analogy helps remember the stages, but the
database records and tests establish actual behavior.

## Repeat without losing your place

The walkthrough adds three controls:

- **Start over** restores the success scenario, step one, and story view,
  clears answers/feedback, hides the quiz, and focuses the step heading.
- **Show/Hide practice question** lets you read first and test recall when
  ready. Moving steps or changing scenario resets the quiz. Changing explanation
  perspective preserves quiz visibility so you can compare story and code.
- **Three terms you can revisit anytime** uses native details/summary markup.
  The glossary remains available without JavaScript, and its open state survives
  lesson rendering because rendering does not replace that part of the page.

The important design is separating `renderPerspective` from full `render`.
A perspective change updates only the explanation. A step change rebuilds the
question and intentionally resets its state. If every button redrew everything,
the learner would lose context unexpectedly.

## Practice with a mistake on purpose

1. Predict what EventCount=0 will do. Run it against a harmless local URL and
   observe parameter rejection before HTTP work.
2. Predict what a whitespace-only EventName will do. Find the check that stops it.
3. Temporarily imagine changing only the payload name and leaving the query
   name fixed. Which assertion would expose that inconsistency?
4. Open a quiz, switch perspective, then change step. Explain why these two
   actions preserve and reset different parts of the state.
5. Open the glossary, restart, and verify that the glossary stays open while
   the exercise resets. Separate reference material from exercise state.

## Teach back

Explain URL encoding without saying "because the function requires it."
Explain why one-element array behavior deserves a boundary check. Then draw
the three pieces of walkthrough state: scenario, step, and perspective; add
quiz visibility and state which actions reset it.

Return tomorrow and run with three events named `purchase`. Before executing,
predict the counts and point to the script variables controlling them. You
have learned the pattern when you can make this change without searching for
the old hardcoded event name.
