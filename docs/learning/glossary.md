# Glossary in the context of Pulse

| Term | Plain meaning | Find it here |
| --- | --- | --- |
| Contract | What a caller can rely on: input, output, errors, and side effects | API contracts and endpoint tests |
| Invariant | A condition that must remain true through success and failure | Queue acknowledgement transaction |
| Entity | A stored object with identity and mutable state | Domain/Entities/Person.cs |
| DTO | A data shape passed across a boundary | ProjectResponse, CaptureRequest |
| Dependency injection | Constructing an object with the services it needs | Program.cs registrations |
| Scope | A lifetime boundary for a set of service instances | IngestionWorker's CreateScope |
| Change tracker | EF's in-memory record of loaded and modified entities | IdentityService and retry cleanup |
| Transaction | Database changes that commit or roll back together | CaptureService |
| Atomicity | The all-or-nothing property of an operation | Replay delete plus queue insert |
| Idempotency | Repeating an operation does not add another intended effect | Identity merging; future capture deduplication |
| Durable | Committed state survives ordinary process restart, subject to storage guarantees | QueuedEvents |
| Acknowledgement | Recording that queued work has been handled | Queue row deletion |
| Dead letter | Failed work retained for investigation and possible recovery | DeadLetterEvent |
| Backpressure | Controlling incoming work when processing cannot keep up | Capture limits; future queue admission policy |
| Eventual consistency | A successful write may become visible through reads later | Capture 202 followed by a trend |
| Tenant boundary | Keeping each customer's/project's data and actions separate | ProjectId query predicates |
| Authentication | Establishing who the caller is | JWT and personal-key handlers |
| Authorization | Checking what that caller may access or change | ProjectAccessService |
| Cursor | A marker for continuing a stable ordered list | ExportService |
| Projection | Selecting only the fields or aggregates needed | Project metrics query |
| Regression test | A test that fails when a previously fixed behavior breaks | IngestionTransactionTests |
| Failure injection | Deliberately making one dependency operation fail | SQLite test triggers |
| Observability | Evidence that helps explain system behavior | Logs, metrics, dead-letter inspection |
| SLO | A measurable service objective over a defined population and period | Design chapter's proposed visibility target |
| ADR | A short record of a design choice and its tradeoffs | docs/adr |
| Lease | Temporary ownership that expires if not renewed | Future export recovery exercise |

When a term feels vague, explain it using one event and one failure in this
repository. If you cannot, return to the linked code path or chapter.
