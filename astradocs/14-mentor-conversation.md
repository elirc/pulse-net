# A conversation you can read aloud

This is a scripted teaching example, not a transcript. Pause after each
learner question and try answering before reading the mentor response.

**Learner:** There are so many files. Do I need to understand them all?

**Mentor:** Start with one route, `/capture`. Find what it receives, which
checks can stop it, what it saves, and what it returns. Other files become
relevant when this path calls them.

**Learner:** I see `CaptureService` in the handler. Does that mean capture
stores the final event right there?

**Mentor:** Follow the call being made. The endpoint uses the service to find
the project by write key, then writes `QueuedEvents` itself. The processor
later calls `IngestQueuedAsync` to store the event. A class name alone does
not tell us which work a particular call performs.

**Learner:** So when I get `202`, nothing has happened yet?

**Mentor:** Something important has happened: pending work has been saved.
Successful background processing is a separate outcome. The worker may
already have finished by the time you look, but the response does not promise it.

**Learner:** Why not put the event in the channel?

**Mentor:** This design keeps the data in SQLite so committed queued work
survives an ordinary process restart. The channel just wakes the worker.
Picture a work ledger and a bell. Losing a ring does not erase the ledger.

**Learner:** I keep confusing User, Person, and distinct ID.

**Mentor:** Mara, who manages the analytics project, is a User. A shop visitor
is a Person. `device-7` is one incoming identifier mapped to that Person.
Say "operator, tracked person, incoming label" and match those to the code names.

**Learner:** If I log in, why might a project still return `404`?

**Mentor:** Login tells Pulse who you are. Membership tells it which projects
you may access. This API hides inaccessible projects using `404`.

**Learner:** Why is there a project filter after membership already passed?

**Mentor:** Consider being a member of A and B. A request to A's route must
still select A's child records. Checking access and selecting the correct
scoped data solve two related problems.

**Learner:** The queue is empty. Surely the event worked?

**Mentor:** A failed event can leave the queue by becoming a dead letter.
Check both failure records and the expected stored/query result.

**Learner:** A transaction means retries cannot duplicate events, right?

**Mentor:** The transaction keeps this queue row's event writes and removal
together. Two separate accepted capture requests still create two rows.
Client-request deduplication needs its own design.

**Learner:** What should I do when I forget this tomorrow?

**Mentor:** Draw `capture -> queue -> worker -> events -> query`. Then find
one matching file for each box. Use the guide to fill the gaps and try once
more without it. The purpose is to practice navigating and reasoning.

**Your turn:** replace Mara's pageview with a purchase event. Retell the
journey and identify which parts change and which stay the same.
