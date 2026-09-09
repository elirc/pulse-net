# Read the capture code slowly

Open [CaptureEndpoints.cs](../src/Pulse.Api/Endpoints/CaptureEndpoints.cs)
beside this page. Read one fragment, then find it in the file. These are
selected source excerpts; the omitted lines still matter to the full route.

## Fragment 1: select a credential

```csharp
var apiKey = request.ApiKey
             ?? http.Request.Headers["X-Api-Key"].FirstOrDefault();
```

| Piece | Read it aloud |
| --- | --- |
| `var apiKey` | Create a local variable; let C# infer its type |
| `request.ApiKey` | Read the key supplied in the request body |
| `??` | If that value is null, use the expression on the right |
| `FirstOrDefault()` | Take the first header value, or the default if none exists |

Important detail: `??` checks null. An empty string in the body is not null,
so it does not fall back to the header. The later whitespace check rejects it.

**Try:** predict the chosen value for a missing body key and a valid header.
Then predict an empty body key and the same valid header.

## Fragment 2: stop on errors

```csharp
var (events, errors) = Unwrap(request);
if (errors.Count > 0)
{
    return Results.ValidationProblem(errors);
}
```

`Unwrap` returns two values: the prepared events and validation errors.
The assignment names both. `if` chooses a branch. `return` ends this
handler, so the queue insertion code below is not reached on this path.

Read it as: "Prepare the input. If any validation errors exist, return them."

## Fragment 3: save pending work

```csharp
db.QueuedEvents.Add(new QueuedEvent
{
    ProjectId = project.Id,
    PayloadJson = JsonSerializer.Serialize(incoming),
});
```

`new` creates an object. The assignments initialize fields through properties.
`Serialize` turns the internal event into JSON text. `Add` tells EF about
the new entity; the later save performs the database write.

## Fragment 4: save, signal, respond

```csharp
await db.SaveChangesAsync(ct);
signal.Ring();
return Results.Accepted(value: new CaptureResponse("queued", events.Count));
```

`await` waits for the operation to complete without requiring this method to
block a thread while waiting. `ct` is the cancellation token passed onward.
The signal wakes the worker. The response says how many events were queued.

This code does not call the trend query. It does not wait for all those
events to be processed before producing the accepted response.

**Check:** why does `Ring()` come after the save?

<details>
<summary>Answer</summary>

The worker should be able to find committed work when it wakes. The queue
is the durable record; the bell only makes discovery faster. A periodic
sweep can discover committed work even if a signal is missed.

</details>

Next: [where these service objects come from](07-startup-and-wiring.md).
