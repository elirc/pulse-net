# Your practice journal

Copy an entry into your own notes for each practice session. The repository's
[implementation journal](journal.md) records the build; this template records
your understanding. They answer different questions.

You do not need to finish a feature in one sitting. Finishing one prediction,
experiment, and explanation is a useful complete learning loop.

## Entry template

**Date and session:**

**Story ID and the behavior I am practicing:**

**The smallest example I will use:**

Write the actual input: a name, a pair of timestamps, two tile IDs, a config
object, or another small fixture. Replace vague phrases such as "test the API"
with a specific action and an observable result.

**My prediction before running anything:**

- HTTP status or command outcome:
- Response fields that matter:
- Database rows that should change:
- Database rows that should remain the same:

**The path I traced:**

```text
input -> endpoint -> authorization -> validation -> service -> database -> response
```

Delete stages that do not apply and add the real file/method names you found.
For asynchronous work, draw the request and worker paths separately and name
the durable row connecting them.

**What I ran and what happened:**

Record the command or request, the observed result, and the relevant assertion.
Avoid copying tokens or project keys into notes. A project ID and a non-secret
test label are usually enough to identify your example.

**Where my prediction differed from the result:**

**My current explanation:**

Use one sentence in ordinary language, then one sentence using the code's
actual terms. For example: "The copy moves independently because it owns a
different tile row. Both tiles retain the same InsightId, so query edits remain
shared."

**One alternative explanation I ruled out, and the evidence:**

**One thing I still do not understand:**

**The next small experiment:**

**Teach-back without looking at the lesson:**

Explain the behavior to an imagined teammate. Then reopen the code and correct
your explanation. Keep the correction; it is evidence of learning, not a mark
against you.

## Example entry: a rejected layout batch

**Behavior:** save two tile positions together.

**Fixture:** tile A uses `x=0,w=6`; tile B uses `x=6,w=6`. Submit `x=6,w=6`
for A and `x=7,w=6` for B.

**Prediction:** 400, because B would extend to column 13. Neither tile changes.

**Trace:** batch layout route -> membership guard -> grid validation -> temporary
layout map. The invalid second input returns before tracked entities are assigned.

**Observed:** record your own result here after running it.

**Explanation:** this endpoint promises one complete layout save. A valid first
item does not earn a partial commit when a later item is invalid.

**Next experiment:** change B to `x=6,w=6`, then check both tiles and a third
unselected tile. The first two should change together; the third should stay put.

## Review an older entry

Return to the same entry after working on a different feature. Before reading
your old answer, predict the result again. Then answer these questions:

1. Can I locate the relevant code without following the lesson's links?
2. Can I explain why the check belongs at this boundary?
3. Can I name a failure case the existing test catches?
4. Can I name a limitation the test does not establish?
5. Can I apply the same idea to a different feature in Pulse?

If one answer is unclear, repeat a smaller example. The objective is a model you
can use when the input changes, not fast recall of a paragraph.
