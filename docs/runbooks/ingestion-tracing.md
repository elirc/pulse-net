# Following an ingestion trace

Capture responses include `X-Trace-Id`. Search local structured ingestion logs for that value. Admission reports queued/duplicate counts through its outcome and duplicate field; processing logs include trace ID, span ID, queue sequence, attempt number, and a bounded outcome. Request logs retain method/status/duration and use route templates to avoid copying route parameter values into the path field.

Queue and dead-letter rows store nullable `TraceParent` and `OriginalTraceParent` columns. Legacy null values and invalid contexts do not prevent processing. Each attempt gets a distinct span. Replay joins the replay request's trace and links to the original producer context. Repeated admission of an already accepted event does not create a worker span unless new work is actually queued.

The named source is `Pulse.Ingestion`. No external telemetry exporter is configured by default, and no collector is required. A future exporter can subscribe to that source; configure its transport and retention separately. Stopping telemetry export must not change ingestion, receipts, or retry behavior. Local fallback correlation remains available when no listener samples a span.

Trace metadata includes only validated W3C identifiers and bounded operational fields. Do not add event payloads, raw distinct IDs, credentials, or arbitrary baggage. A trace ID is untrusted diagnostic context, never an authorization token, deduplication key, or proof of successful processing. For durable outcome, inspect the processing receipt; for authorized management changes, inspect audit history.

See [bootcamp session 22](../../astradocs/bootcamp/22-ingestion-tracing.md) for parent/link diagrams and a failure/replay exercise.
