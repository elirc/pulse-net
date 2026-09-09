# When you get stuck

Being stuck is a situation to describe. You do not need to solve the whole
system at once. Find the first place where the result differs from your
prediction.

## Choose the row matching what you see

| Observation | Check first | Small next action |
| --- | --- | --- |
| `dotnet` not recognized | SDK installation/PATH | Follow [setup](../docs/learning/01-first-session.md) |
| Compiler error before tests run | First error's file and location | Read surrounding code and the error before editing |
| Application Control blocks an assembly | Environment policy, not an assertion | Record code/path; use an approved environment for execution |
| Connection refused | API process and listening URL | Compare the terminal's URL with the demo's `-BaseUrl` |
| HTTP 400 from capture | Request field names and required values | Check `event` and `distinct_id` in CaptureContracts/validation |
| HTTP 401 | Credential type and placement | Consult [keys](09-keys-and-permissions.md) |
| HTTP 404 for a project | ID, membership, or missing resource | Verify with the owner account; do not assume the URL alone is wrong |
| 202, but no event in the query | Pending work, dead letters, range, filter | Inspect project metrics and query inputs |
| Queue timeout | Last metrics included in the error | Compare backlog observations and inspect worker failures |
| Count is 2 but unique persons is 1 | Identity mappings | Check whether both events resolve to the same Person |

The recorded Windows runtime block and large-batch timeout are
[unresolved validation issues](../docs/learning/validation.md). Do not read
their appearance as proof that you misunderstood an exercise.

## Describe one example precisely

```text
I sent: one pageview for device-7 in project P1.
I expected: one matching event in the selected UTC day.
I observed: 202 accepted, then zero matching events.
I checked: pending count, dead letters, event name, project, date range.
My current hypothesis is:
One observation that could disprove it is:
```

Replace the illustrative values with your actual observations. Keep keys,
tokens, and personal payload data out of shared debugging notes.

## If the code itself is overwhelming

Choose one method. Write its input, output, and side effects. A side effect
is something it changes, such as inserting a database row. Ignore helper
internals until you know why the method calls them.

For `ReplayAsync`: inputs are project ID, letter ID, cancellation token;
output is a replay outcome; the success side effect moves a stored letter
back to the queue. That is a useful first explanation before studying SQL.

## Ask for help with a small question

"I understand that capture saves a queue row. I cannot see where the person
is created. Can you point me to the method and let me trace it?"

That question gives a reviewer a clear place to start. After a hint, repeat
the explanation in your own words and locate its source.
