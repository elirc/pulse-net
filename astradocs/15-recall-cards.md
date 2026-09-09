# Recall cards: answer before opening

Try four cards at a time. Say an answer, open the explanation, and mark it
"comfortable", "needed a hint", or "revisit" in your own notes. There is no
score to maximize. Follow the repair link when an answer still feels vague.

## 1. What does Pulse do?

<details><summary>Answer and another way to say it</summary>

It collects product activity, processes it, and answers analytics questions.
Another version: actions enter; query answers come out. [Picture it](02-big-picture.md).

</details>

## 2. What is a project?

<details><summary>Answer and example</summary>

A project groups analytics data and its access. Mara's shop and a different
shop can have separate projects. [Find the objects](08-people-and-tables.md).

</details>

## 3. User or Person: which one logs into management?

<details><summary>Answer and contrast</summary>

User. Person models tracked product activity. A visitor sending a pageview
does not thereby get a management account. [Review the mapping](08-people-and-tables.md).

</details>

## 4. What does `distinct_id` mean?

<details><summary>Answer and example</summary>

An incoming product identifier, such as `device-7`. A project-scoped mapping
connects it to a Person ID. Several distinct IDs can refer to one Person.
[Follow the rows](08-people-and-tables.md).

</details>

## 5. What has happened when capture returns 202?

<details><summary>Answer and limit</summary>

Accepted work has been saved to the queue. The response does not guarantee
that background processing succeeded. [Retell the story](04-one-event-story.md).

</details>

## 6. Where is the queued event payload: channel or database?

<details><summary>Answer and analogy</summary>

Database. The channel is the bell; `QueuedEvents` is the work ledger.
[See the diagram](02-big-picture.md).

</details>

## 7. Can the queue be empty while an event has failed?

<details><summary>Answer and what to check</summary>

Yes. Dead-lettered work also leaves the queue. Inspect dead letters and the
expected event/query result. [Use the diagnostic guide](13-when-stuck.md).

</details>

## 8. Why are event writes and queue removal in one transaction?

<details><summary>Answer and failure example</summary>

They must commit together. Otherwise a crash after the event save but before
queue removal could leave work that creates another event on retry.
[Read the worked feature](../docs/learning/worked-example-ingestion.md).

</details>

## 9. Do those transactions deduplicate two HTTP capture requests?

<details><summary>Answer and limitation</summary>

No. Separate accepted requests can create separate queue rows. Client
idempotency is an additional design problem. [Hear it explained](14-mentor-conversation.md).

</details>

## 10. Why can a signed-in caller get 404 for a project?

<details><summary>Answer and vocabulary</summary>

They may not belong to the project. Authentication identifies the caller;
authorization checks access. [Review credentials](09-keys-and-permissions.md).

</details>

## 11. Two matching events by one resolved person: what are the counts?

<details><summary>Answer and condition</summary>

For the same trend bucket: event count 2, unique persons 1.
[Look at the data shapes](05-data-shapes.md).

</details>

## 12. Where would you investigate a wrong trend count?

<details><summary>Answer and first checks</summary>

Start with InsightEndpoints and QueryService. Check project, event name,
range, filters, and ingestion outcome before changing arithmetic.
[Find the files](03-repository-map.md).

</details>

## 13. What does `await db.SaveChangesAsync(ct)` mean?

<details><summary>Answer in plain language</summary>

Ask EF to save tracked changes, passing cancellation, and continue after
that operation completes. If an explicit transaction surrounds it, saving
does not itself mean that surrounding transaction has committed.
[Read the code slowly](06-code-close-up.md).

</details>

## 14. Does a 50% flag randomly flip on each call?

<details><summary>Answer and qualification</summary>

No. Rollout uses a deterministic hash. The same inputs and unchanged
targeting/configuration give a stable decision. [Visit flags](10-feature-tour.md).

</details>

## 15. What does a saved insight store?

<details><summary>Answer and analogy</summary>

A query definition, like a recipe for computing an answer. A dashboard tile
can refer to that definition. [Visit dashboards](10-feature-tour.md).

</details>

## 16. Which test would establish rollback in SQLite?

<details><summary>Answer and reason</summary>

An infrastructure test using real SQLite, an injected write failure, and
assertions about stored state. A success-only fake cannot establish that
behavior. [Review test boundaries](11-tests-as-examples.md).

</details>

## 17. What happens to an old event when replay is accepted?

<details><summary>Answer and limitation</summary>

The valid stored letter is consumed and its payload is queued at the tail
with a fresh retry budget. It can fail again, and original processing order
is not restored. [Read operations guidance](../docs/runbooks/ingestion.md).

</details>

## 18. Is failing before an assembly loads the same as a failed assertion?

<details><summary>Answer and next step</summary>

No. The behavior assertion has not executed. Record the load/environment
error separately. [Check the validation record](../docs/learning/validation.md).

</details>

**Transfer exercise:** invent one new question about the same five core
ideas. Write the answer and link the source code that supports it.
