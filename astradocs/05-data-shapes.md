# The same event in four shapes

**Keep one sentence:** the HTTP request, pending work, stored event, and
query answer describe related things, but they are different data structures.

The following is an illustrative trace for a fresh project. Labels `P1`
and `Person-A` stand for GUIDs; they are not values to paste into API calls.

## Shape 1: JSON sent over HTTP

```json
{
  "api_key": "pk_live_REPLACE_WITH_LOCAL_PROJECT_KEY",
  "event": "pageview",
  "distinct_id": "device-7",
  "timestamp": "2026-09-08T10:00:00Z",
  "properties": { "page": "/pricing" }
}
```

`event` names the action. `distinct_id` identifies the tracked visitor.
`properties` gives details of that action. The placeholder key will not
authenticate; the [demo](../scripts/learning-demo.ps1) obtains real local keys.

## Shape 2: the internal envelope

After unwrapping, the endpoint creates an `IncomingEvent`:

```csharp
new IncomingEvent(
    "pageview",
    "device-7",
    new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero),
    "{\"page\":\"/pricing\"}");
```

The properties object has become a string containing JSON. That is why
quotes appear escaped in this C# string. It still means `page` is `/pricing`.

A `QueuedEvent` stores the serialized envelope in `PayloadJson`, plus the
project ID, queue sequence, enqueue time, and retry count. The original
request's API key is not a field of this internal event envelope.

## Shape 3: processed database records

Conceptually, after a successful first event:

| Collection | Selected fields |
| --- | --- |
| Persons | `Id=Person-A`, `ProjectId=P1`, `PropertiesJson={}` |
| PersonDistinctIds | `ProjectId=P1`, `DistinctId=device-7`, `PersonId=Person-A` |
| Events | `ProjectId=P1`, `Name=pageview`, `PersonId=Person-A`, event properties and timestamp |
| EventDefinitions | An observed event name: `pageview` |
| PropertyDefinitions | An observed property name/type: `page`, `string` |
| QueuedEvents | This successfully handled row has been removed |

The event's ordinary `page` property does not automatically become a person
property. Person updates use `$set` or `$set_once` within event properties.

## Shape 4: a query answer

For a daily trend covering the event, one bucket can look like this:

```json
{ "start": "2026-09-08T00:00:00+00:00", "count": 1, "uniquePersons": 1 }
```

This is an aggregate answer, not the original event object. A second event
by the same resolved person on that day makes count 2 while uniquePersons
stays 1, assuming both match the query.

**Repeat:** a request asks for work; a queue row holds pending work; an event
records processed activity; a trend summarizes matching activity.

Next: [read the actual endpoint lines](06-code-close-up.md).
