# 25 web-platform user stories with follow-along implementation plans

**Status: proposed work only. Nothing in this document is implemented.** Every
example below describes behavior you would add and verify. Existing features
used as starting points are identified separately in each story.

These stories extend the 75-story bootcamp
([junior](../astradocs/18-junior-user-stories.md),
[midlevel](../astradocs/19-midlevel-feature-user-stories.md),
[mid-senior](../astradocs/20-mid-senior-feature-user-stories.md)). That program
covered validation, authorization, pagination, transactions, workers, leases,
schema upgrades, retention, erasure and bounded queries very thoroughly. It did
**not** cover most of the HTTP protocol itself, the browser security model,
outbound network calls, streaming responses, or the operational contract a web
service owes its clients.

That is the gap these 25 stories fill. They are written to the same routine, use
the same code, and are verifiable the same way.

## Pick a story

### Tier 1 â€” Foundations (no schema change, one or two files)

| ID | User-visible improvement | Main practice |
| --- | --- | --- |
| [WEB-01](#web-01) | Browsers stop guessing content types on API responses | Middleware ordering and response headers |
| [WEB-02](#web-02) | A throttled SDK learns when to retry | `429` semantics, `Retry-After`, `RateLimit-*` |
| [WEB-03](#web-03) | Support can trace one customer request through the logs | Request correlation and `traceparent` |
| [WEB-04](#web-04) | A deploy stops sending traffic to a starting instance | Liveness vs readiness |
| [WEB-05](#web-05) | A client pages a list without building URLs by hand | `Link` headers (RFC 8288) |
| [WEB-06](#web-06) | Wrong request/response media types fail clearly | `415`, `406`, `Accept` negotiation |
| [WEB-07](#web-07) | A trend is visible in a browser without any tooling | Fetch, safe rendering, accessible tables |

### Tier 2 â€” HTTP contracts (may add a column or a small table)

| ID | User-visible improvement | Main practice |
| --- | --- | --- |
| [WEB-08](#web-08) | A browser app on another origin can call the API | CORS, preflight, credentialed requests |
| [WEB-09](#web-09) | SDKs stop re-downloading unchanged flag definitions | `ETag`, `If-None-Match`, `304` |
| [WEB-10](#web-10) | Two editors stop silently overwriting each other | `If-Match`, `412`, optimistic concurrency |
| [WEB-11](#web-11) | A retried create stops making duplicates | `Idempotency-Key` |
| [WEB-12](#web-12) | Large JSON responses transfer faster | Compression and correct `Vary` |
| [WEB-13](#web-13) | An interrupted export download resumes | `Range`, `206`, `ETag` validators |
| [WEB-14](#web-14) | Person properties import from a CSV file | Streaming multipart uploads |
| [WEB-15](#web-15) | A client survives an API change | Versioning, `Deprecation`, `Sunset` |
| [WEB-16](#web-16) | A 500-row bulk edit reports exactly what failed | `207`, partial success, per-item errors |
| [WEB-17](#web-17) | "Daily" means the customer's day, not UTC | Time zones, DST, bucket boundaries |

### Tier 3 â€” Platform (design work, integration, measurement)

| ID | User-visible improvement | Main practice |
| --- | --- | --- |
| [WEB-18](#web-18) | Rate limits count the caller who actually authenticated | Limiter identity and endpoint filters |
| [WEB-19](#web-19) | A customer's system is notified when an alert fires | Outbound HTTP, HMAC signing, SSRF defense |
| [WEB-20](#web-20) | A new teammate receives an invitation email | Transactional outbox, single-use tokens |
| [WEB-21](#web-21) | An operator watches the queue drain live | Server-sent events, heartbeats, backpressure |
| [WEB-22](#web-22) | A deploy finishes in-flight work instead of dropping it | Graceful shutdown and draining |
| [WEB-23](#web-23) | A client generates itself from the API description | OpenAPI generation and contract tests |
| [WEB-24](#web-24) | A browser session survives a refresh without exposing a token | Cookies, `SameSite`, CSRF, rotation |
| [WEB-25](#web-25) | Ten dashboards refresh at once without queueing | Read concurrency, WAL, measurement |

---

## Before you start

**These stories assume the current working tree.** Run `git status --short`
first. The checkout contains a large amount of uncommitted work (see the
[engineering report](engineering-report.md) Â§2). Commit or otherwise secure it
before adding to it; otherwise your change is stacked on top of something that
does not exist anywhere else.

**Constraints that apply to every story below**

- **No new NuGet packages** unless the story names one. The application
  currently has four total; that is a deliberate property worth preserving.
  Where a story needs something (OpenAPI, compression), it names the package
  and says why the built-in option was rejected.
- **SQLite stays.** None of these stories require a different database. WEB-25
  is explicitly about measuring the current one, not replacing it.
- **The permission matrix is the source of truth.** Any new project-scoped
  route must be added to
  [ProjectPermissionMatrix](../src/Pulse.Api/Auth/ProjectPermissionMatrix.cs)
  with an explicit role and scope. A route that is missing from it has no
  restricted-token enforcement, which is a silent authorization hole rather
  than a visible error.
- **404 for non-members, 403 for insufficient role.** Do not invent a new
  denial convention; see [ADR-0002](../docs/adr/0002-404-not-403-membership.md).
- **Every failure returns RFC 7807 problem details.** Use
  [InputRules.Problem](../src/Pulse.Api/Endpoints/InputRules.cs) or
  `Results.ValidationProblem` rather than a bare status code with no body.
- **Middleware order matters and is not obvious.** The current pipeline is
  exception handler â†’ status code pages â†’ request logging â†’ rate limiter â†’
  authentication â†’ authorization â†’ restricted-token middleware â†’ endpoints.
  Several stories below insert middleware; each one says where and why. Putting
  it in the wrong place usually still compiles and still passes the happy path.
- **Time comes from `TimeProvider`.** It is registered as a singleton in
  [Program.cs](../src/Pulse.Api/Program.cs). Never call `DateTimeOffset.UtcNow`
  in code you intend to test against a boundary.
- **Tests boot the real host.** [PulseApiFactory](../tests/Pulse.Tests/PulseApiFactory.cs)
  runs migrations against a private shared-cache in-memory SQLite database with
  background workers live. [TestAuth](../tests/Pulse.Tests/TestAuth.cs) registers
  users; [TestIngestion.WaitForDrainAsync](../tests/Pulse.Tests/TestIngestion.cs)
  waits for capture to persist. Use them rather than inventing fixtures.

**The routine to use for every story**

1. Read the story once. Write down the expected request, status code and
   response shape *before* opening an editor.
2. Add the smallest failing test that expresses that expectation. Confirm it
   fails for the reason you predicted. A compile error is not a failing
   assertion.
3. Implement one numbered step. Inspect the result. Stop between steps freely.
4. Run the focused filter named in the story, then the full suite before you
   call it done.
5. Update [api-reference.md](../docs/api-reference.md) and, where the story says
   so, [architecture.md](../docs/architecture.md) and the relevant runbook.
6. Write the review note: problem, change, evidence, remaining limitation. Every
   one of these stories has a remaining limitation. Naming it is part of the work.

---

# Tier 1 â€” Foundations

<a id="web-01"></a>
## WEB-01 â€” Stop browsers from reinterpreting API responses

**User story:** As a security reviewer, I want the API to send standard
protective response headers so a browser cannot be tricked into treating a JSON
response as script or embedding the API in a hostile frame.

**Current starting point:** the pipeline in
[Program.cs](../src/Pulse.Api/Program.cs) sets no response headers beyond what
ASP.NET Core adds. Export downloads return attacker-influenced bytes
(`Results.Bytes` over a stored document) with no `nosniff` header. **Proposed
scope:** one middleware adding four fixed headers. No configuration, no
per-route behavior.

**Open:** [Program.cs](../src/Pulse.Api/Program.cs),
[RequestLoggingMiddleware.cs](../src/Pulse.Api/RequestLoggingMiddleware.cs) as
the shape to copy, and
[ProductionReadinessTests.cs](../tests/Pulse.Tests/Api/ProductionReadinessTests.cs).

**Target example:** `GET /health` returns 200 with
`X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`,
`Referrer-Policy: no-referrer`, and a `Content-Security-Policy` of
`default-src 'none'; frame-ancestors 'none'`.

### Implementation steps

1. Add `src/Pulse.Api/SecurityHeadersMiddleware.cs`. Copy the constructor and
   `InvokeAsync` shape from `RequestLoggingMiddleware`.
2. Register the headers inside `context.Response.OnStarting(...)`, **not** by
   assigning them before `await _next(context)`. Once a handler starts writing
   the body the headers are already sent and a late assignment throws.
   `OnStarting` runs at the last safe moment.
3. Use the indexer (`headers["X-Frame-Options"] = "DENY"`) rather than `Add`.
   `Add` throws on a duplicate key and a handler may set one of these itself.
4. Register it in `Program.cs` **before** `UseMiddleware<RequestLoggingMiddleware>`
   so the headers also apply to responses produced by the exception handler.
   Verify by asserting on a deliberately failing request.
5. Choose the CSP deliberately. `default-src 'none'` is right for a pure JSON API
   and wrong the moment you serve HTML â€” WEB-07 serves a page, so write the
   policy so that story has an obvious place to relax it, and say so in a comment.
6. Add `SecurityHeaderTests`: assert all four headers on a 200, a 404, a 400
   validation problem, and an export download.
7. Document the headers and the CSP choice in
   [architecture.md](../docs/architecture.md) under "Cross-cutting".

### Acceptance and verification

All four headers appear on success, client errors, server errors, and binary
download responses. No response carries a duplicate header. Values are
byte-identical across routes.

Run `dotnet test --filter "FullyQualifiedName~SecurityHeaderTests"`, then the
full suite â€” a middleware ordering mistake surfaces as unrelated failures.

**Common mistake:** setting headers after `await _next(...)`, which works in
tests that return small buffered responses and fails on streamed ones.
**Done when:** headers are present on every status class. **Explain:** why does
`nosniff` matter specifically on the export download route?

---

<a id="web-02"></a>
## WEB-02 â€” Tell a throttled SDK when to try again

**User story:** As an SDK author, I want a 429 response to tell me how long to
wait and how much budget I have left, so I can back off correctly instead of
guessing or hammering the endpoint.

**Current starting point:** `Program.cs` configures a fixed-window limiter on
`/capture` with `RejectionStatusCode = 429` and `QueueLimit = 0`. The rejection
carries no body and no headers, so a client cannot distinguish "wait one second"
from "wait a minute". **Proposed scope:** an `OnRejected` callback plus headers
on success. Do not change the limiting algorithm or its partition key â€” that is
WEB-18.

**Open:** [Program.cs](../src/Pulse.Api/Program.cs) (the `AddRateLimiter` block)
and [ProductionReadinessTests.cs](../tests/Pulse.Tests/Api/ProductionReadinessTests.cs),
which already contains a rate-window recovery test you must not break.

**Target example:** the 301st capture inside one window returns 429 with
`Retry-After: 43`, `RateLimit-Limit: 300`, `RateLimit-Remaining: 0`,
`RateLimit-Reset: 43`, and a problem-details body titled `Too many requests`.

### Implementation steps

1. Read the existing rate-window test first and note its timing assumptions.
   Whatever you add must keep passing under its five-second window.
2. Set `options.OnRejected`. Its `OnRejectedContext` exposes
   `Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)` â€” the
   fixed-window limiter supplies this metadata.
3. Write `Retry-After` as **whole seconds, rounded up**. A value of `0` tells the
   client to retry immediately and defeats the limit; clamp to a minimum of 1.
4. Write the `RateLimit-Limit` / `RateLimit-Remaining` / `RateLimit-Reset` trio.
   `Remaining` is `0` on the rejection path by definition. Note in a comment that
   these are the draft IETF names and that some clients look for the older
   `X-RateLimit-*` spelling instead.
5. Write a problem-details body from `OnRejected` by setting
   `context.HttpContext.Response.StatusCode` and serializing the JSON yourself â€”
   `IResult` is not available in that callback. Set the content type to
   `application/problem+json` exactly.
6. For successful requests, emit `RateLimit-Remaining` from the acquired lease
   via an endpoint filter on the capture route. Accept that the value is
   approximate under concurrency and document that rather than implying precision.
7. Add `RateLimitHeaderTests` using a derived factory that configures a very low
   permit limit. Assert headers and body on rejection, and that `Remaining`
   decreases across successful calls.
8. Document all five headers, their units, and the approximation caveat in
   [api-reference.md](../docs/api-reference.md) and the
   [ingestion runbook](../docs/runbooks/ingestion.md).

### Acceptance and verification

A rejected request carries `Retry-After` of at least 1 and a problem body.
Waiting the advertised duration and retrying succeeds. Successful responses carry
a decreasing `RateLimit-Remaining`. The existing rate-window recovery test still
passes.

Run `dotnet test --filter "FullyQualifiedName~RateLimit"` and
`--filter "FullyQualifiedName~ProductionReadinessTests"`.

**Common mistake:** lowering the permit limit in the shared test factory and
breaking every other ingestion test. Use a derived factory. **Done when:** a
client can implement correct backoff from the response alone. **Explain:** why is
`Retry-After` expressed in seconds rather than as a timestamp, and when is the
other form better?

---

<a id="web-03"></a>
## WEB-03 â€” Trace one customer request through the logs

**User story:** As a support engineer, I want every response to carry a request
id that also appears in the log line and in error bodies, so a customer can quote
one value and I can find exactly what happened.

**Current starting point:**
[RequestLoggingMiddleware](../src/Pulse.Api/RequestLoggingMiddleware.cs) logs
`TraceId` and `SpanId` from `Activity.Current` and sets `X-Trace-Id` **only on
`/capture`** via `IngestionTrace.Producer`. Every other route logs a trace id the
caller never receives. **Proposed scope:** accept or generate a correlation id
for every request, echo it, log it, and surface it in problem bodies.

**Open:** [RequestLoggingMiddleware.cs](../src/Pulse.Api/RequestLoggingMiddleware.cs),
[Program.cs](../src/Pulse.Api/Program.cs) (the `AddProblemDetails` call), and
[BoundaryTests.cs](../tests/Pulse.Tests/Api/BoundaryTests.cs).

**Target example:** `GET /api/projects` sent with
`traceparent: 00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01` returns
`X-Request-Id: 4bf92f3577b34da6a3ce929d0e0e4736`, and the log line for that
request contains the same value. A request without `traceparent` gets a
generated id.

### Implementation steps

1. Decide the precedence rule and write it down before coding: inbound
   `traceparent` trace-id, then inbound `X-Request-Id`, then
   `Activity.Current.TraceId`, then a newly generated value. Resolving this
   ambiguity is the actual difficulty of the story.
2. **Validate the inbound value.** It is attacker-controlled and it goes into
   logs. Accept at most 64 characters matching `[A-Za-z0-9._-]` and otherwise
   generate your own. An unvalidated id containing a newline lets a caller forge
   log lines.
3. Resolve the id at the top of `InvokeAsync`, store it in `HttpContext.Items`
   under a constant key, and set the response header via `OnStarting`.
4. Wrap the existing log call in a scope
   (`_logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = id })`)
   so the id attaches to handler logs too, not only the summary line.
5. Extend `AddProblemDetails` with a `CustomizeProblemDetails` callback copying
   the id into `problemDetails.Extensions["requestId"]`. This is the value
   customers actually quote.
6. Keep the existing `/capture` `X-Trace-Id` behavior intact â€” ingestion tracing
   depends on it. Emit both headers on that route rather than replacing one.
7. Add `RequestCorrelationTests`: echoed on success; echoed on a 400 and present
   in the body's `requestId`; generated when absent; sanitized when hostile (send
   a value containing a newline and assert replacement); preserved when valid.
8. Document the header, the precedence rule, and the sanitization bound in
   [api-reference.md](../docs/api-reference.md).

### Acceptance and verification

Every response carries `X-Request-Id`. Problem bodies carry a matching
`requestId`. Hostile values never reach a log line. The `/capture` trace header
is unchanged.

Run `dotnet test --filter "FullyQualifiedName~RequestCorrelation"`.

**Common mistake:** trusting the inbound header. **Done when:** one id ties
response, body and logs together. **Explain:** why is an id you accept from the
client more useful than one you always generate â€” and what does that trust cost?

---

<a id="web-04"></a>
## WEB-04 â€” Stop sending traffic to an instance that is not ready

**User story:** As an operator, I want separate liveness and readiness probes so
a starting or draining instance is removed from load balancing without being
killed and restarted.

**Current starting point:** one `/health` endpoint in
[Program.cs](../src/Pulse.Api/Program.cs) counts `QueuedEvents` and
`DeadLetterEvents`, returns 503 when the database throws, and reports `degraded`
(still 200) above 10,000 pending. It conflates three questions: is the process
alive, can it serve traffic, and is the backlog healthy. **Proposed scope:** add
`/health/live` and `/health/ready`; leave `/health` unchanged.

**Open:** [Program.cs](../src/Pulse.Api/Program.cs),
[DatabaseLifecycle.cs](../src/Pulse.Infrastructure/Schema/DatabaseLifecycle.cs),
and [ProductionReadinessTests.cs](../tests/Pulse.Tests/Api/ProductionReadinessTests.cs).

**Target example:** `GET /health/live` returns 200 with `{"status":"live"}` even
while the database is unreachable. `GET /health/ready` returns 503 under the same
condition and 200 with
`{"status":"ready","checks":{"database":"ok","schema":"current"}}` when healthy.

### Implementation steps

1. Write the semantic contract in a comment first: **liveness answers "should the
   orchestrator kill me"; readiness answers "should the load balancer send me
   traffic".** A liveness probe that checks a dependency causes restart storms
   when that dependency blips. That mistake is the reason this story exists.
2. Map `/health/live` as a constant response. It must not touch the database, not
   create a scope, and not be async.
3. Map `/health/ready` to one cheap connectivity check
   (`db.Database.CanConnectAsync`) plus a schema check reusing
   `DatabaseLifecycle.InspectAsync`. Return 503 unless the state is `current`.
4. Add a singleton `ReadinessState` with a `bool Ready` set to `true` after the
   startup initializer completes, and report 503 before that. This gate is what
   makes the probe meaningful during startup.
5. Exclude both new paths from `RequestLoggingMiddleware` alongside the existing
   `/health` exclusion, or probe traffic dominates the logs.
6. Leave `/health` byte-identical and describe it in the docs as the combined
   legacy probe. Changing it breaks whatever already watches it.
7. Add `ReadinessProbeTests`: live returns 200 always; ready returns 503 before
   initialization; 200 after; 503 when the schema state is not current â€” use a
   factory whose initializer is a no-op so migrations never run.
8. Document all three probes, their meanings, and which one an orchestrator
   should use for which purpose in [architecture.md](../docs/architecture.md) and
   [api-reference.md](../docs/api-reference.md).

### Acceptance and verification

Liveness succeeds with a broken database. Readiness fails before initialization,
succeeds after, and fails on a stale schema. Neither appears in request logs.
`/health` behaves exactly as before.

Run `dotnet test --filter "FullyQualifiedName~ReadinessProbe"` plus the existing
`ProductionReadinessTests`.

**Common mistake:** checking the database in the liveness probe. **Done when:**
each probe answers exactly one question. **Explain:** what happens to a fleet if
liveness checks a shared database and that database has a two-minute outage?

---

<a id="web-05"></a>
## WEB-05 â€” Page through a list without constructing URLs

**User story:** As an API client, I want `next` and `prev` links in the response
headers so I can page by following links instead of reimplementing offset
arithmetic and the limit cap.

**Current starting point:** list endpoints accept `limit` (max 500) and `offset`
and return a bare JSON array. Clients must know the cap, track offsets, and infer
the end from a short page. Export routes already use opaque cursors
([ADR-0007](../docs/adr/0007-cursor-pagination-for-exports.md)) and are **out of
scope** â€” do not convert them.

**Open:** [ProjectEndpoints.cs](../src/Pulse.Api/Endpoints/ProjectEndpoints.cs),
[PersonEndpoints.cs](../src/Pulse.Api/Endpoints/PersonEndpoints.cs),
[InputRules.cs](../src/Pulse.Api/Endpoints/InputRules.cs), and
[BoundaryTests.cs](../tests/Pulse.Tests/Api/BoundaryTests.cs).

**Target example:** `GET /api/projects/{id}/persons?limit=2&offset=2` on a
project with five persons returns 200 and a `Link` header containing
`rel="next"` at offset 4 and `rel="prev"` at offset 0.

### Implementation steps

1. Add `src/Pulse.Api/Endpoints/PageLinks.cs` with one method taking the
   `HttpContext`, the applied limit, the applied offset, and the returned row
   count, and writing the header.
2. Decide how "is there a next page" is answered. With offset paging you cannot
   know without either a count query or fetching one extra row. **Fetch
   `limit + 1` and return `limit`** â€” one extra row is far cheaper than a second
   `COUNT(*)` over the same predicate. Record that reasoning in the file.
3. Emit `prev` only when `offset > 0`, clamping its offset at zero rather than
   producing a negative value.
4. Build the URL from `Request.Path` plus the **existing** query string with
   `limit` and `offset` replaced â€” not from a hardcoded template. A client
   filtering by `nameContains` must get that filter back in the link. Use
   `QueryHelpers.AddQueryString` over a dictionary copy of `Request.Query`.
5. Percent-encode correctly. A `nameContains` value containing a space and an
   ampersand must survive the round trip; assert exactly that case.
6. Apply it to three list routes first â€” persons, projects, annotations â€” and
   leave the rest for a follow-up. A consistent partial rollout beats a rushed
   complete one.
7. Add `PaginationLinkTests`: a middle page has both links; the first page has
   only `next`; the last has only `prev`; a single-page result has neither;
   filters and encoding survive; following `next` repeatedly enumerates every row
   exactly once with no duplicates and no gaps.
8. Document the header, the `rel` values, and explicitly that these links are
   **offset-based and not stable under concurrent writes** â€” a row inserted
   between requests can be skipped or repeated. That limitation is precisely why
   exports use cursors, and stating it teaches the difference.

### Acceptance and verification

Following links from the first page visits every row once on a static dataset.
Filters are preserved. Encoding round-trips. Export routes are untouched.

Run `dotnet test --filter "FullyQualifiedName~PaginationLink"`.

**Common mistake:** rebuilding the URL from scratch and silently dropping every
other query parameter. **Done when:** a client can page using only the header.
**Explain:** why can offset paging skip a row, and what does the export cursor do
differently?

---

<a id="web-06"></a>
## WEB-06 â€” Fail clearly on the wrong media type

**User story:** As a client developer, I want a precise error when I send the
wrong `Content-Type` or ask for a response format the endpoint cannot produce, so
I fix my request instead of debugging a confusing parse failure.

**Current starting point:** JSON endpoints accept any `Content-Type` â€” the body
binds regardless, so a form-encoded POST fails as a JSON parse error (400, "Body
must be valid JSON") rather than 415. Export routes select CSV or JSON from a
`?format=` query parameter and ignore `Accept` entirely. **Proposed scope:** 415
on unsupported request media types; `Accept` negotiation with 406 on the three
export routes. `?format=` keeps working and wins when both are present.

**Open:** [LimitedJsonBody.cs](../src/Pulse.Api/Endpoints/LimitedJsonBody.cs),
[ExportEndpoints.cs](../src/Pulse.Api/Endpoints/ExportEndpoints.cs) (see
`TryParseFormat` and `FormatProblem`),
[CaptureRequestParser.cs](../src/Pulse.Api/Endpoints/CaptureRequestParser.cs),
and [ExportTests.cs](../tests/Pulse.Tests/Api/ExportTests.cs).

**Target example:** `POST /api/projects` with
`Content-Type: application/x-www-form-urlencoded` returns 415 with a problem body
naming `application/json`. `GET /api/projects/{id}/export/events` with
`Accept: text/csv` returns CSV; with `Accept: application/xml` returns 406.

### Implementation steps

1. Add a `RequiresJsonBody` endpoint filter. Accept a missing `Content-Type` only
   when there is no body; accept `application/json` and any `application/*+json`;
   accept a `charset` parameter; reject everything else with 415.
2. Parse with `MediaTypeHeaderValue.TryParse`, not string comparison.
   `application/json; charset=utf-8` must pass and `application/jsonfoo` must not.
3. Apply the filter to the JSON route groups. **Do not apply it to `/capture`:**
   that route deliberately accepts beacon-style requests from browser SDKs, which
   commonly send `text/plain`. Comment the exception â€” it is the interesting part
   of this story.
4. Add `src/Pulse.Api/Endpoints/AcceptNegotiation.cs` that parses `Accept`,
   honors `q` weights, resolves `*/*` and `text/*`, and returns the chosen format
   or `null` when nothing is acceptable.
5. In the export handlers resolve the format as: explicit `?format=` wins;
   otherwise negotiate `Accept`; otherwise default to JSON as today. Return 406
   only when `Accept` is present, non-wildcard, and unsatisfiable.
6. Set the response content type to the negotiated value (`text/csv` or
   `application/json`) and add `Vary: Accept`. Without `Vary` a cache serves one
   client's CSV to another asking for JSON â€” WEB-12 depends on this being right.
7. Add `MediaTypeTests`: form-encoded POST returns 415; JSON with charset
   succeeds; `application/vnd.api+json` succeeds; capture with `text/plain` still
   returns 202; `Accept: text/csv` yields CSV; `application/xml` yields 406;
   `text/csv;q=0.2, application/json;q=0.9` yields JSON; `?format=csv` with
   `Accept: application/json` yields CSV; `Vary` is present on every export
   response.
8. Document the accepted request types, the capture exception, the negotiation
   precedence, and the `Vary` header.

### Acceptance and verification

Wrong request types return 415 and never reach the handler. Negotiation respects
`q` values. `?format=` always wins. `/capture` still accepts `text/plain`.
`Vary: Accept` appears on every negotiated response.

Run `dotnet test --filter "FullyQualifiedName~MediaType"` then `~ExportTests`.

**Common mistake:** substring-matching the header and accepting
`application/jsonp`. **Done when:** every rejection names the acceptable types.
**Explain:** why is `Vary: Accept` required, and what breaks without it?

---

<a id="web-07"></a>
## WEB-07 â€” See a trend in a browser with no build tooling

**User story:** As an evaluator, I want a single static page that signs in, picks
a project, and draws a trend, so I can see the platform work without writing a
client first.

**Current starting point:** the platform is API-only. The one browser artifact is
[astradocs/index.html](../astradocs/index.html), an offline lesson walkthrough
with no network access, verified by
[scripts/verify-walkthrough.mjs](../scripts/verify-walkthrough.mjs). **Proposed
scope:** one self-contained page, no framework, no bundler, no npm dependency.
Deliberately a demonstration page, not a product UI.

**Relationship to WEB-08:** serving the page from the API itself sidesteps CORS
entirely. Do that here, and treat WEB-08 as the follow-up that makes the same
page work from a different origin.

**Open:** [astradocs/index.html](../astradocs/index.html) for house style,
[Program.cs](../src/Pulse.Api/Program.cs),
[InsightEndpoints.cs](../src/Pulse.Api/Endpoints/InsightEndpoints.cs) for the
trend response shape, and
[scripts/verify-walkthrough.mjs](../scripts/verify-walkthrough.mjs) as the
verifier pattern to copy.

**Target example:** `GET /console` serves a page with an email/password form.
After signing in it lists the caller's projects, and choosing one draws a 30-day
`pageview` trend as an inline SVG bar chart with an accessible data table beneath.

### Implementation steps

1. Create `src/Pulse.Api/wwwroot/console.html`, add `app.UseStaticFiles()`, and
   map `GET /console` to return it. Confirm `wwwroot` reaches the build output.
2. Relax the WEB-01 CSP for this path only, or scope the strict policy to `/api`.
   A `default-src 'none'` policy blocks your own script. Set `script-src 'self'`
   and put the JavaScript in a separate `console.js` rather than reaching for
   `unsafe-inline` â€” feeling that constraint is part of the exercise.
3. Post credentials to `/api/auth/login`, read the JWT, and hold it **in a
   JavaScript variable, not `localStorage`**. Comment that `localStorage` is
   readable by any injected script and that WEB-24 replaces this properly.
4. Call `GET /api/projects`, render a `select`, then call
   `GET /api/projects/{id}/insights/trend?event=pageview&interval=day` with the
   `Authorization` header.
5. Render every server value with `textContent` or `createTextNode` â€” **never**
   `innerHTML`. A project named with an HTML tag and an inline event handler is a
   legitimate name and must render as literal text. Create exactly that project
   in the verifier.
6. Draw the chart as inline SVG computed from the bucket values. No chart
   library. Give the SVG `role="img"` and a `title`, and render the same numbers
   in a real table with a `caption` and `th scope="col"` so the data is reachable
   without vision.
7. Handle the states a real client must: network failure, a 401 (expired token â€”
   return to the form), a project with zero events (draw an empty axis, not a
   blank page), and a slow response (disable the button, show a pending state).
8. Add `scripts/verify-console.mjs` following the structure of the existing
   walkthrough verifier: start the API on a disposable database, seed a project
   and a few events, drive the page headlessly, and assert login, the project
   list, the bar count, table contents, the hostile-name case rendering as text,
   and the empty and 401 states. Copy its temporary-profile and graceful-shutdown
   handling â€” that script exists in its current form because cleanup was hard.
9. Document the page, its scope, and its explicit non-goals in
   [getting-started.md](../docs/getting-started.md).

### Acceptance and verification

The page signs in, lists projects, draws a chart matching the API response, and
renders a hostile project name as visible text. Keyboard navigation reaches every
control, and the table conveys the same data as the chart. The verifier exits 0.

Run `node scripts/verify-console.mjs`.

**Common mistake:** `innerHTML` with server data, and storing the token in
`localStorage` because every tutorial does. **Done when:** the page is useful and
the injection case is proven inert. **Explain:** the API never renders HTML â€” so
where does this XSS risk actually come from, and who owns fixing it?

---

# Tier 2 â€” HTTP contracts

<a id="web-08"></a>
## WEB-08 â€” Let a browser app on another origin call the API

**User story:** As a frontend developer, I want to call the API from my own
web app on a different origin, so I can build a UI without proxying every
request through my own server.

**Current starting point:** no CORS configuration exists anywhere â€” no
`AddCors`, no `UseCors`. Every cross-origin browser request currently fails at
the preflight stage with no useful error. **Proposed scope:** a configured
origin allowlist, correct preflight handling, and exposure of the response
headers the earlier stories added. **Never `AllowAnyOrigin`.**

**Open:** [Program.cs](../src/Pulse.Api/Program.cs),
[appsettings.json](../src/Pulse.Api/appsettings.json), and
[ProductionReadinessTests.cs](../tests/Pulse.Tests/Api/ProductionReadinessTests.cs).

**Target example:** with `Cors:AllowedOrigins` set to
`https://app.example.com`, an `OPTIONS` preflight for
`PUT /api/projects/{id}` from that origin returns 204 with
`Access-Control-Allow-Origin`, `Access-Control-Allow-Methods`,
`Access-Control-Allow-Headers: Authorization, Content-Type, If-Match` and
`Access-Control-Max-Age`. The same preflight from `https://evil.example.com`
returns 204 with **no** `Access-Control-Allow-Origin` header.

### Implementation steps

1. Read the configured origins from `Cors:AllowedOrigins` as a string array.
   Default to **empty**, meaning no cross-origin access. A safe default that
   must be opted into is the whole point.
2. Reject wildcards in configuration at startup with a clear
   `InvalidOperationException`. `AllowAnyOrigin` combined with
   `AllowCredentials` is invalid per spec and ASP.NET will throw at runtime
   instead â€” catching it at startup is better.
3. Build one named policy with `WithOrigins(configured)`,
   `WithMethods("GET","POST","PUT","DELETE","OPTIONS")`, and
   `WithHeaders("Authorization","Content-Type","If-Match","If-None-Match",
   "Idempotency-Key","X-Api-Key","X-Request-Id")`. Add each header as the story
   that introduces it lands.
4. Call `WithExposedHeaders("Link","ETag","X-Request-Id","Retry-After",
   "RateLimit-Limit","RateLimit-Remaining","RateLimit-Reset","X-Next-Cursor")`.
   **This is the step people forget.** Browser JavaScript cannot read any
   response header that is not exposed, so WEB-02's and WEB-05's headers are
   invisible to a browser client without it, even though curl shows them.
5. Set `SetPreflightMaxAge(TimeSpan.FromHours(1))` so a browser does not
   preflight every single request.
6. Place `app.UseCors(policy)` **after** `UseRouting` (implicit in minimal APIs)
   and **before** `UseAuthentication`. Ordering matters: a preflight carries no
   credentials, so if authentication runs first the `OPTIONS` request is
   rejected with 401 and the browser reports an opaque CORS failure.
7. Decide credentials explicitly. Bearer tokens in a header do **not** need
   `AllowCredentials`. Only WEB-24's cookie session does. Leave it off here and
   let WEB-24 turn it on together with its origin restriction; write that
   dependency in a comment.
8. Add `CorsTests` driving raw `OPTIONS` requests with `Origin` and
   `Access-Control-Request-Method` headers: allowed origin gets the headers;
   unlisted origin gets none; a request with no `Origin` is unaffected; exposed
   headers list matches what the other stories emit; preflight succeeds without
   an `Authorization` header.
9. Document the setting, the default-deny behavior, and the exposed header list
   in [architecture.md](../docs/architecture.md) and
   [getting-started.md](../docs/getting-started.md).

### Acceptance and verification

Preflight from an allowed origin succeeds without credentials. An unlisted
origin receives no allow header. Non-browser clients are entirely unaffected.
Every header the API emits and a browser needs is in the exposed list.

Run `dotnet test --filter "FullyQualifiedName~CorsTests"`.

**Common mistake:** believing CORS protects the API. It does not â€” it protects
*other people's browsers* from your API. curl ignores it completely.
**Done when:** an allowed origin works and an unlisted one silently gets
nothing. **Explain:** why does a failed CORS check produce an opaque error in
the browser rather than a useful message?

---

<a id="web-09"></a>
## WEB-09 â€” Stop re-downloading unchanged flag definitions

**User story:** As an SDK doing local flag evaluation, I want to poll for
definitions cheaply, so a project whose flags have not changed costs a small
304 instead of a full payload every 30 seconds.

**Current starting point:**
`GET /api/projects/{id}/feature-flags/local-evaluation` returns full flag
definitions on every call. It is designed to be polled, so it is the highest
value caching target in the API. There is no `ETag` anywhere in the codebase.

**Open:** [FeatureFlagEndpoints.cs](../src/Pulse.Api/Endpoints/FeatureFlagEndpoints.cs),
[FeatureFlagService.cs](../src/Pulse.Infrastructure/Services/FeatureFlagService.cs),
[FlagVersion.cs](../src/Pulse.Domain/Entities/FlagVersion.cs), and
[FeatureFlagTests.cs](../tests/Pulse.Tests/Api/FeatureFlagTests.cs).

**Target example:** a first `GET` returns 200 with
`ETag: "3f9a1c2e"` and `Cache-Control: private, max-age=0, must-revalidate`.
Repeating it with `If-None-Match: "3f9a1c2e"` returns **304 with no body**.
Editing any flag changes the tag and the next request returns 200.

### Implementation steps

1. Choose the validator source before writing code. Two options: hash the
   serialized response, or derive from stored state (max `UpdatedAt` plus a row
   count, or the project's newest `FlagVersion` id). **Hash the serialized
   response bytes** â€” it cannot drift from what you actually return, and this
   payload is small. Note the cost: you compute the body to decide you did not
   need to send it. That is an honest trade and worth a comment.
2. Use SHA-256 over the serialized bytes, take the first 8 bytes as lowercase
   hex, and format it as a **quoted** string. The quotes are required by the
   spec; omitting them is the classic bug and some proxies will drop the header.
3. This is a **strong** validator, so no `W/` prefix. Write a comment on why
   strong is correct here: byte-for-byte equality, which matters because WEB-13
   uses validators for range requests where weak ones are not permitted.
4. Compare `If-None-Match` correctly: it may carry a comma-separated list, and
   `*` matches anything present. Do not use plain string equality on the raw
   header.
5. Return 304 with `Results.StatusCode(304)`, and set the `ETag` header on the
   304 as well â€” clients need it to keep revalidating. Ensure the body is empty;
   a 304 with a body is malformed and some clients hang on it.
6. Set `Cache-Control: private, max-age=0, must-revalidate`. `private` matters:
   this payload is project-scoped and a shared cache must never reuse it across
   callers. Add `Vary: Authorization` for the same reason.
7. Confirm the response varies with the **caller's authorization**, not just the
   project. If a restricted token would see a different payload than a full
   member, the tag must differ too, or one caller's cached copy leaks the other's
   view. Write a test for exactly this even if the payloads are identical today â€”
   it will fail loudly if someone later makes the response caller-dependent.
8. Add `FlagCachingTests`: first call returns a tag; repeat with the tag returns
   304 and no body; after an edit the tag changes and a 200 returns; a
   nonsense tag returns 200; a list containing the current tag returns 304; `*`
   returns 304; the 304 carries the tag; two different callers get consistent
   behavior.
9. Document the headers, the validator semantics, and a suggested polling
   interval in [api-reference.md](../docs/api-reference.md).

### Acceptance and verification

Unchanged definitions return 304 with an empty body. Any flag mutation
invalidates the tag. Malformed and list-valued `If-None-Match` values behave per
spec. `Vary: Authorization` is present.

Run `dotnet test --filter "FullyQualifiedName~FlagCaching"` then `~FeatureFlagTests`.

**Common mistake:** an unquoted tag, or a 304 that still carries a body.
**Done when:** a polling SDK transfers almost nothing while flags are stable.
**Explain:** you still built the whole response to return 304 â€” what did the
client actually save, and what did the server not save?

---

<a id="web-10"></a>
## WEB-10 â€” Stop two editors from silently overwriting each other

**User story:** As an editor, I want my update rejected if someone else changed
the same dashboard since I loaded it, so I do not silently destroy their work.

**Current starting point:** `PUT` handlers load the entity, assign fields, and
save. Two editors who both loaded version A and both save produce a last-write-
wins result with no indication anything was lost. The codebase already
understands this problem in the worker layer â€” `AttemptGeneration` and owner
tokens fence stale export and ingestion writers â€” but the HTTP layer has no
equivalent.

**Depends on WEB-09** for the `ETag` helper.

**Open:** [DashboardEndpoints.cs](../src/Pulse.Api/Endpoints/DashboardEndpoints.cs),
[InsightEndpoints.cs](../src/Pulse.Api/Endpoints/InsightEndpoints.cs),
[Dashboard.cs](../src/Pulse.Domain/Entities/Dashboard.cs),
[PulseDbContext.cs](../src/Pulse.Infrastructure/PulseDbContext.cs), and
[DashboardTests.cs](../tests/Pulse.Tests/Api/DashboardTests.cs).

**Target example:** `GET /api/projects/{p}/dashboards/{d}` returns
`ETag: "7"`. `PUT` with `If-Match: "7"` succeeds and returns `ETag: "8"`. A
second `PUT` with the stale `If-Match: "7"` returns **412** and changes nothing.

### Implementation steps

1. Add a `RowVersion` integer column to `Dashboard` and `Insight`, defaulting to
   1. Create the migration. Backfill existing rows to 1 â€” do not leave nulls;
   the whole mechanism depends on every row having a comparable value.
2. Do **not** rely on EF's `IsConcurrencyToken` with SQLite's `rowversion`
   emulation. Increment the column explicitly in the handler and make the
   `UPDATE` conditional. Being explicit is clearer to read and does not depend on
   provider-specific behavior.
3. Emit `ETag` from `RowVersion` on the single-item `GET` for both resources.
   Quote it. Reuse WEB-09's formatting helper so the two stories cannot drift.
4. Decide and document whether `If-Match` is **required** or **optional** on
   `PUT`. Recommendation: optional for now, returning 428 `Precondition Required`
   only when a per-project setting demands it. Making it mandatory immediately
   breaks every existing client, and that migration cost is the real lesson.
5. Implement the check as a conditional update inside one transaction:
   `UPDATE ... SET fields, RowVersion = RowVersion + 1 WHERE Id = @id AND
   RowVersion = @expected`, then inspect the affected row count. **Do not**
   read-then-compare-then-write â€” that is the same race with extra steps and it
   is the mistake this story exists to prevent.
6. Return 412 with a problem body naming the current version when the affected
   count is zero and the row still exists. Return 404 when the row is gone.
   Distinguishing those two is part of the contract.
7. Return the new `ETag` on the successful `PUT` so a client can chain edits
   without re-fetching.
8. Add `ConcurrencyTests`: happy path increments; a stale tag returns 412 and
   leaves every field untouched; a missing header still succeeds; `*` succeeds
   when the row exists; a deleted row returns 404; and â€” the important one â€” two
   updates issued concurrently against the same starting tag produce exactly one
   success and one 412. Drive that last case with `Task.WhenAll` over two clients.
9. Document the header, both status codes, and which resources support it.

### Acceptance and verification

Concurrent conflicting updates yield one success and one 412. A rejected update
leaves the stored row byte-identical. `ETag` values advance monotonically per row.

Run `dotnet test --filter "FullyQualifiedName~ConcurrencyTests"` then `~DashboardTests`.

**Common mistake:** comparing versions in application code before issuing an
unconditional `UPDATE`. **Done when:** the concurrent test proves exactly one
winner. **Explain:** how is this the same idea as the export worker's
`AttemptGeneration` fence, and why does each layer need its own?

---

<a id="web-11"></a>
## WEB-11 â€” Stop a retried create from making duplicates

**User story:** As a client that retries on timeout, I want to send the same
create request twice and get the same single resource back, so a network failure
after the server committed does not leave me with two dashboards.

**Current starting point:** ingestion already solves this for `/capture` with
project-scoped client UUIDs deduplicating matching retries for seven days
(`CaptureAdmissionKey`, `CaptureFingerprint`). The management API has nothing:
a retried `POST /dashboards` creates a second dashboard.

**Open:** [CaptureFingerprint.cs](../src/Pulse.Infrastructure/Services/CaptureFingerprint.cs)
and [CaptureAdmission.cs](../src/Pulse.Domain/Entities/CaptureAdmission.cs) as
the pattern to generalize,
[DashboardEndpoints.cs](../src/Pulse.Api/Endpoints/DashboardEndpoints.cs),
[ExportEndpoints.cs](../src/Pulse.Api/Endpoints/ExportEndpoints.cs), and
[lesson 16](../astradocs/bootcamp/16-admission-identity-and-receipts.md).

**Target example:** `POST /api/projects/{p}/dashboards` with
`Idempotency-Key: 6f1b...` returns 201 and the new dashboard. The identical
request repeated returns **200** with the same body and
`Idempotency-Replayed: true`. The same key with a *different* body returns 422.

### Implementation steps

1. Read lesson 16 first and reuse its vocabulary. This story generalizes an
   existing, tested idea rather than inventing one â€” recognizing that is half
   the exercise.
2. Add an `IdempotencyRecord` entity: project id, key, request fingerprint,
   route, response status, response body, created-at. Unique index on
   (project id, key). Create the migration.
3. Fingerprint the **method, route pattern, and canonical request body** â€” not
   the raw bytes. Two semantically identical JSON bodies with different key
   ordering or whitespace must match. Canonicalize by parsing and re-serializing
   with sorted properties.
4. Implement it as an endpoint filter so it applies uniformly, not as code
   copied into each handler. Handlers that forget the copy are the failure mode.
5. Get the ordering right, and write it down before coding:
   - No key present â†’ behave exactly as today.
   - Key present, no record â†’ run the handler, then store key, fingerprint,
     status and body **in the same transaction as the handler's own writes**.
     Storing it afterwards leaves a window where the work committed and the
     record did not, which is the exact bug this story prevents.
   - Key present, record exists, fingerprint matches â†’ return the stored
     response with `Idempotency-Replayed: true`.
   - Key present, record exists, fingerprint differs â†’ 422 naming the conflict.
6. Handle the concurrent case: two identical requests arriving simultaneously
   both find no record. The unique index makes one insert fail â€” catch the
   constraint violation, re-read, and return the stored response. Do not assume
   the check protects you; the index is what protects you.
7. Only store responses for **successful** outcomes (2xx). Replaying a stored
   400 prevents a client from ever fixing its request with the same key. Document
   that decision explicitly; the alternative is defensible but must be chosen.
8. Bound the key: 1â€“255 characters, printable ASCII. Add a cleanup path â€” reuse
   the pattern in
   [CaptureMetadataCleanupWorker.cs](../src/Pulse.Api/Ingestion/CaptureMetadataCleanupWorker.cs)
   rather than writing a new worker â€” and pick a retention window, stating that
   a retry after expiry creates a second resource. That is the same finite-
   lifetime limitation already recorded for capture deduplication.
9. Apply to `POST /dashboards`, `POST /insights`, `POST /exports`. Add all three
   to the docs; leave the rest for a follow-up.
10. Add `IdempotencyTests`: replay returns the identical body and the replay
    header; a differing body returns 422; no key behaves as before; two
    concurrent identical posts create exactly **one** row (assert by counting);
    an expired record creates a new resource; a failed request stores nothing.

### Acceptance and verification

A replayed create returns the original resource and creates nothing. Concurrent
duplicates create exactly one row. A key reused with a different body is
rejected. Cleanup removes expired records without touching live ones.

Run `dotnet test --filter "FullyQualifiedName~Idempotency"`.

**Common mistake:** writing the idempotency record in a separate transaction
from the work it describes. **Done when:** the concurrent count assertion passes
repeatedly. **Explain:** why is the unique index, not the existence check, what
actually makes this correct?

---

<a id="web-12"></a>
## WEB-12 â€” Make large responses transfer faster

**User story:** As a client on a slow connection, I want large JSON and CSV
responses compressed, so a 40,000-row export does not take a minute to arrive.

**Current starting point:** no compression is configured. Export documents are
stored as text and returned whole; trend responses with breakdowns can be large.
These payloads are highly compressible.

**Depends on WEB-06** for correct `Vary` handling.

**Open:** [Program.cs](../src/Pulse.Api/Program.cs),
[ExportEndpoints.cs](../src/Pulse.Api/Endpoints/ExportEndpoints.cs), and
[ExportTests.cs](../tests/Pulse.Tests/Api/ExportTests.cs).

**Target example:** `GET .../export/events?format=csv` with
`Accept-Encoding: br, gzip` returns `Content-Encoding: br` and
`Vary: Accept-Encoding, Accept`, with a body several times smaller than the
uncompressed form. The same request without `Accept-Encoding` returns the plain
body.

### Implementation steps

1. Use the built-in `AddResponseCompression` â€” no package needed. Enable the
   Brotli and Gzip providers and set both to `CompressionLevel.Fastest`.
   `Optimal` costs substantially more CPU for a few percent of size on JSON;
   measure both rather than taking that on faith.
2. Add `text/csv` and `application/problem+json` to `MimeTypes`. The defaults do
   not include either, and CSV is the payload that benefits most here.
3. Place `UseResponseCompression` **first** in the pipeline, before the security
   headers middleware. It must wrap the response stream before anything writes
   to it.
4. Leave `EnableForHttps` **off** and write a comment explaining why: compressing
   a response that mixes secret content with attacker-influenced content over TLS
   is the BREACH attack. Then reason about whether this API qualifies â€” export
   bodies contain customer data and query parameters the caller controls. The
   conclusion is defensible either way; an unexamined `EnableForHttps = true` is
   not.
5. Verify `Vary: Accept-Encoding` is emitted and that it **combines** with
   WEB-06's `Vary: Accept` rather than replacing it. Appending to `Vary` with the
   indexer overwrites; this is the single most common bug in this story and the
   reason it depends on WEB-06.
6. Confirm compression does not break the `Content-Length`-sensitive paths. A
   compressed response is chunked; assert that download consumers still read the
   full body.
7. Add `CompressionTests`: Brotli preferred when offered; gzip when only gzip is
   offered; identity when nothing is offered; compressed and uncompressed bodies
   decode to identical bytes; both `Vary` values present; a small response is
   handled correctly.
8. Measure and record actual before/after sizes for a 10,000-row CSV export in
   the [exports runbook](../docs/runbooks/exports.md). A ratio you measured beats
   a ratio you assumed.

### Acceptance and verification

Compressed responses decode to exactly the uncompressed bytes. Encoding
negotiation follows `Accept-Encoding`. Both `Vary` values are present together.
Measured ratios are recorded.

Run `dotnet test --filter "FullyQualifiedName~Compression"` then `~ExportTests`.

**Common mistake:** overwriting `Vary` instead of appending, so a cache serves a
Brotli body to a client that cannot decode it. **Done when:** both `Vary` values
coexist and the round trip is byte-exact. **Explain:** what is BREACH, and does
this API meet its preconditions?

---

<a id="web-13"></a>
## WEB-13 â€” Resume an interrupted export download

**User story:** As an analyst downloading a large export over a poor connection,
I want to resume from where the transfer stopped, so I do not restart a
40 MB download from zero.

**Current starting point:** `GET .../exports/{jobId}/download` returns
`Results.Bytes(...)` over the stored document with no `Accept-Ranges`, no
validator, and no range support. The job's content is immutable once completed,
which makes it an ideal range target.

**Depends on WEB-09** for validator formatting.

**Open:** [ExportEndpoints.cs](../src/Pulse.Api/Endpoints/ExportEndpoints.cs)
(the download handler, around line 282),
[ExportJob.cs](../src/Pulse.Domain/Entities/ExportJob.cs), and
[ExportLifecycleTests.cs](../tests/Pulse.Tests/Api/ExportLifecycleTests.cs).

**Target example:** the download returns `Accept-Ranges: bytes`,
`Content-Length: 40960` and an `ETag`. Repeating with `Range: bytes=1024-2047`
returns **206** with `Content-Range: bytes 1024-2047/40960` and exactly 1024
bytes. An unsatisfiable range returns **416**.

### Implementation steps

1. Check first whether `Results.Bytes` already handles this. It accepts
   `enableRangeProcessing` and an `entityTag`. **Use the framework's
   implementation** rather than writing range parsing yourself â€” then read what
   it does, because the point of the story is understanding the semantics, not
   reimplementing them.
2. Compute a strong `ETag` for the completed document, reusing WEB-09's helper.
   The document is immutable once `Status == Completed`, so hashing it once and
   storing the result on the job row is better than rehashing per request. Add
   the column and migration.
3. Add `Last-Modified` from `CompletedAt` and support `If-Range` â€” a client
   resuming after the resource changed must get the whole new body, not a
   spliced mixture of two documents. This is the subtle correctness point.
4. Set `Accept-Ranges: bytes` on the full response so clients know to try.
5. Return 416 with a `Content-Range: bytes */{length}` header for an
   unsatisfiable range. The header on the error is required and routinely omitted.
6. Decide what happens for multi-range requests (`bytes=0-99,200-299`). The
   framework may return the full body instead of a multipart response. Whatever
   it does, test it and document it rather than leaving it undefined.
7. Note the real limitation in the docs: the document is stored inline in the
   database (see the comment on `ExportJob`), so a range request still loads the
   whole document into memory to slice it. The client saves bandwidth; the server
   saves nothing. Fixing that means object storage, which is out of scope â€”
   naming the gap is in scope.
8. Add `RangeDownloadTests`: full download unchanged; a mid-file range returns
   206 with exact bytes and correct `Content-Range`; an open-ended `bytes=1024-`
   works; a suffix `bytes=-512` works; an out-of-bounds range returns 416 with
   the header; `If-Range` with a stale validator returns the full 200;
   concatenating sequential ranges reproduces the original bytes exactly.
9. Document the headers, the 416 contract, and the in-memory limitation in
   [api-reference.md](../docs/api-reference.md) and the
   [exports runbook](../docs/runbooks/exports.md).

### Acceptance and verification

Concatenated ranges reproduce the full document byte-for-byte. Unsatisfiable
ranges return 416 with `Content-Range`. `If-Range` with a stale validator falls
back to a full response.

Run `dotnet test --filter "FullyQualifiedName~RangeDownload"` then `~ExportLifecycleTests`.

**Common mistake:** off-by-one on the inclusive end. `bytes=0-1023` is 1024
bytes, not 1023. **Done when:** sequential ranges reassemble exactly.
**Explain:** why must `If-Range` use a strong validator?

---

<a id="web-14"></a>
## WEB-14 â€” Import person properties from a CSV file

**User story:** As an analyst, I want to upload a CSV of person properties, so I
can enrich people with plan tier and account age from our billing system without
replaying events.

**Current starting point:** every write is JSON. Nothing in the codebase reads
multipart uploads. `Csv.cs` in the domain project handles CSV *writing* for
exports; reading is new. `IdentityService` and `PersonPropertyMerger` already
implement the `$set`/`$set_once` merge rules this must reuse.

**Open:** [Csv.cs](../src/Pulse.Domain/Csv.cs),
[PersonPropertyMerger.cs](../src/Pulse.Domain/PersonPropertyMerger.cs),
[IdentityService.cs](../src/Pulse.Infrastructure/Services/IdentityService.cs),
[PersonEndpoints.cs](../src/Pulse.Api/Endpoints/PersonEndpoints.cs), and
[LimitedJsonBody.cs](../src/Pulse.Api/Endpoints/LimitedJsonBody.cs) as the size-
limiting pattern to follow.

**Target example:** `POST /api/projects/{p}/persons/import` with a multipart
body containing a CSV whose first column is `distinct_id` returns 200 with
`{"matched":412,"updated":410,"skipped":2,"errors":[...]}`.

### Implementation steps

1. Write the CSV contract before any code: first row is a header; first column
   must be `distinct_id`; remaining columns become person properties; quoting
   follows RFC 4180. Ambiguity here produces a parser nobody can predict.
2. Reuse `PersonPropertyMerger` for the write semantics. Add a `mode` form field
   accepting `set` or `set_once`. Do not reimplement merge rules â€” the identity
   tests already pin that behavior and reimplementation will drift from it.
3. Enforce limits **before** reading: reject `Content-Length` over 10 MB, cap
   rows at 50,000, cap columns at 50, cap cell length at 1,000. Follow
   `LimitedJsonBody` â€” it already demonstrates checking the declared length and
   then enforcing the real one while reading, because `Content-Length` can lie.
4. Read with `request.ReadFormAsync` and set
   `MultipartBodyLengthLimit`/`ValueLengthLimit` on the form options. The
   defaults are generous and are a denial-of-service surface on an authenticated
   but low-privilege route.
5. Stream the file section rather than buffering it whole. Read with a
   `StreamReader` over `section.Body` line by line. Buffering a 10 MB upload per
   concurrent request is exactly the mistake this story is designed to avoid.
6. Write a real CSV reader in `Pulse.Domain` â€” quoted fields, embedded commas,
   doubled quotes for a literal quote, CRLF and LF line endings. Put it next to
   `Csv.cs` and unit-test it in `tests/Pulse.Tests/Domain` with no HTTP involved.
   This is a pure-logic component and belongs in the pure-logic project.
7. Decide the failure policy explicitly and document it: **process valid rows,
   collect per-row errors, cap the reported error list at 100.** All-or-nothing
   on a 50,000-row import means one typo wastes the whole upload; partial success
   means the caller must read the response. Both are defensible, one must be
   chosen.
8. Batch the database writes â€” process in chunks of 500 inside a transaction per
   chunk, not one transaction for 50,000 rows and not one per row. Explain the
   choice in a comment.
9. Unknown `distinct_id` values do **not** create persons. Count them as
   `skipped`. Creating people from an uploaded file would let an import invent
   identities that never emitted an event, which breaks the identity model.
10. Add the route to `ProjectPermissionMatrix` as Editor with the
    `configuration:write` scope, and confirm a restricted token lacking that
    scope is rejected.
11. Add `PersonImportTests`: happy path; quoted fields with commas; both line
    endings; a UTF-8 BOM; a missing `distinct_id` column returning 400; an
    unknown id counted as skipped; `set` vs `set_once` semantics; oversized file
    returning 413; too many rows returning 400; a malformed row reported without
    aborting the rest; a non-member returning 404.
12. Document the format, limits, modes, the failure policy, and the response
    shape in [api-reference.md](../docs/api-reference.md).

### Acceptance and verification

A well-formed CSV updates exactly the named persons with the requested merge
semantics. Malformed rows are reported without discarding valid ones. Every
limit returns the correct status. Unknown ids never create persons.

Run `dotnet test --filter "FullyQualifiedName~PersonImport"` and
`--filter "FullyQualifiedName~CsvReader"`.

**Common mistake:** splitting on commas. A single quoted field containing a
comma breaks it, and real exports are full of them. **Done when:** the quoting
tests pass and memory does not scale with file size. **Explain:** why should
this route refuse to create persons?

---

<a id="web-15"></a>
## WEB-15 â€” Change the API without breaking existing clients

**User story:** As an API consumer, I want a stable version to pin to and clear
warning when something is going away, so an improvement on your side is not an
outage on mine.

**Current starting point:** routes are unversioned (`/api/projects/...`). There
is no deprecation mechanism, so any breaking change is silent. The API has 110
routes and no generated description, so consumers cannot even diff it.

**Open:** [Program.cs](../src/Pulse.Api/Program.cs) (the `MapGroup` calls),
[ProjectPermissionMatrix.cs](../src/Pulse.Api/Auth/ProjectPermissionMatrix.cs),
[api-reference.md](../docs/api-reference.md), and any endpoint file.

**Target example:** `GET /v1/projects` and `GET /api/projects` return identical
responses; the unversioned form additionally returns
`Deprecation: true`, `Sunset: Wed, 01 Apr 2026 00:00:00 GMT` and
`Link: <https://.../docs/api-reference.md>; rel="deprecation"`.

### Implementation steps

1. Pick a versioning strategy and justify it in an ADR before coding. URL path
   (`/v1/`), a header (`Api-Version: 1`), or media type
   (`application/vnd.pulse.v1+json`). **Recommendation: URL path** â€” it is
   visible in logs, cacheable, and trivially testable, which matters more here
   than purity. Write ADR-0011 recording the alternatives.
2. Do **not** add a versioning package. Mount the existing endpoint groups under
   a `/v1` prefix alongside the current `/api` prefix. `MapGroup` composes, so
   the same registration function serves both.
3. **`ProjectPermissionMatrix.Normalize` matches on route text.** Two prefixes
   for the same handler means two keys, or a normalization step that strips the
   prefix before lookup. Get this right first: a route missing from the matrix
   has no restricted-token enforcement and fails open. Add a test that asserts
   every registered project route resolves to a matrix rule under **both**
   prefixes â€” enumerate `EndpointDataSource` rather than listing them by hand.
4. Add a `Deprecated(DateTimeOffset sunset, string link)` endpoint filter
   emitting `Deprecation`, `Sunset` (in HTTP-date format, which is **not** ISO
   8601) and a `Link` with `rel="deprecation"`.
5. Apply it to the `/api` group only. Nothing about behavior changes â€” only the
   headers. A deprecation that changes behavior is not a deprecation.
6. Log deprecated-route usage at Information with the route and the caller's
   project id, so you can tell whether anyone still depends on it before the
   sunset date. Without this the sunset date is a guess.
7. Write the compatibility policy into
   [architecture.md](../docs/architecture.md): what counts as breaking (removing
   a field, tightening validation, changing a status code) versus additive
   (adding an optional field or parameter), and the minimum notice period.
   Writing this down is the actual deliverable; the headers are mechanism.
8. Add `VersioningTests`: both prefixes return identical bodies for several
   routes; only the legacy prefix carries deprecation headers; the `Sunset` value
   parses as an HTTP-date; the permission matrix resolves every route under both
   prefixes; a restricted token is enforced identically on both.
9. Update every example in [api-reference.md](../docs/api-reference.md) and
   [getting-started.md](../docs/getting-started.md) to `/v1`.

### Acceptance and verification

Both prefixes are functionally identical. Authorization is identical on both â€”
verified by enumeration, not by spot check. Deprecation headers appear only on
the legacy prefix and parse correctly.

Run `dotnet test --filter "FullyQualifiedName~Versioning"` then the full suite.

**Common mistake:** adding the prefix and leaving the permission matrix keyed on
the old text, silently disabling scope enforcement on the new routes.
**Done when:** the enumeration test proves both prefixes are covered.
**Explain:** why is tightening validation on an existing field a breaking change?

---

<a id="web-16"></a>
## WEB-16 â€” Report exactly which items in a bulk edit failed

**User story:** As an operator adding 500 people to a cohort, I want one request
that tells me precisely which ids failed and why, so I do not send 500 requests
or lose 499 good rows to one bad id.

**Current starting point:** `POST .../cohorts/{id}/persons` and
`POST .../ingestion/dead-letters/replay-batch` exist, and the replay-batch route
has already confronted this problem â€” read what it returns before designing
anything. Most other collection routes are single-item.

**Open:** [CohortEditingEndpoints.cs](../src/Pulse.Api/Endpoints/CohortEditingEndpoints.cs),
[IngestionEndpoints.cs](../src/Pulse.Api/Endpoints/IngestionEndpoints.cs) (the
replay-batch handler), [CohortEditingService.cs](../src/Pulse.Infrastructure/Services/CohortEditingService.cs),
and [CohortEditingTests.cs](../tests/Pulse.Tests/Api/CohortEditingTests.cs).

**Target example:** posting 4 person ids where 2 succeed, 1 is unknown and 1 is
already a member returns **207** with a per-item array giving each id an index, a
status and a reason.

### Implementation steps

1. Read the existing replay-batch response shape first and **match it**. Two
   different partial-success conventions in one API is worse than either
   convention alone. If it needs changing, change both together.
2. Decide the semantics before coding, and write them in the handler comment:
   all-or-nothing (one transaction, any failure rolls back) versus per-item
   (each item independent). **Per-item, with 207**, for bulk membership â€” but the
   choice must be conscious, because a partially applied cohort edit is a real
   state a caller must handle.
3. Return 207 only for genuinely mixed outcomes. All success returns 200; all
   failure returns 400. A client that gets 207 must read the body; one that gets
   200 should not have to. Returning 207 unconditionally defeats the point.
4. Give each result item a stable `index` matching the request array position.
   Callers correlate by position, not by echoing the id â€” an id may legitimately
   appear twice.
5. Use a stable machine-readable `code` per item (`not_found`,
   `already_member`, `invalid`) alongside human-readable text. Client logic keys
   on the code; the message is for people.
6. Cap the batch at 500 items and reject larger with 400 naming the cap. An
   uncapped bulk endpoint is a denial-of-service vector on an authenticated route.
7. Process in one transaction per chunk, and make each item's failure isolated
   within it â€” a constraint violation on item 3 must not abandon items 4 through
   500. Test with a duplicate id deliberately included.
8. Be explicit about duplicates within one request: the second occurrence reports
   `already_member` for the same id added twice. Assert it.
9. Add `BulkOperationTests`: all-success returns 200; all-failure returns 400;
   mixed returns 207; indexes align with the request order; duplicates behave as
   specified; over-cap returns 400; a partial failure leaves successful items
   committed; the shape matches replay-batch.
10. Document the status codes, the per-item shape, the code vocabulary, and the
    cap in [api-reference.md](../docs/api-reference.md).

### Acceptance and verification

Mixed outcomes return 207 with per-item results in request order. Successful
items persist despite sibling failures. All-success and all-failure use plain
status codes. The response shape matches the existing batch route.

Run `dotnet test --filter "FullyQualifiedName~BulkOperation"` then `~CohortEditingTests`.

**Common mistake:** returning 207 for every batch, forcing clients to parse the
body even when nothing went wrong. **Done when:** the three outcome classes are
distinguishable from the status line alone. **Explain:** when is all-or-nothing
the right choice for a batch, and why is cohort membership not that case?

---

<a id="web-17"></a>
## WEB-17 â€” Make "daily" mean the customer's day

**User story:** As an analyst in Berlin, I want daily trends bucketed by my local
day, so a report for Monday covers Monday where my customers live rather than a
UTC window that starts at 01:00 or 02:00 depending on the season.

**Current starting point:**
[TimeBucket.Truncate](../src/Pulse.Domain/TimeBucket.cs) converts to UTC and
truncates. Every trend, retention cohort and hourly alert window is therefore a
UTC boundary. For any customer outside UTC, "daily active users" is wrong at the
edges â€” and wrong by a different amount twice a year.

**This is the hardest correctness story in the tier.** Read it fully before
starting.

**Open:** [TimeBucket.cs](../src/Pulse.Domain/TimeBucket.cs),
[QueryService.cs](../src/Pulse.Infrastructure/Services/QueryService.cs),
[BoundedQueryService.cs](../src/Pulse.Infrastructure/Services/BoundedQueryService.cs),
[Project.cs](../src/Pulse.Domain/Entities/Project.cs),
[TimeBucketTests.cs](../tests/Pulse.Tests/Domain/TimeBucketTests.cs), and
[ADR-0006](../docs/adr/0006-utc-ticks-datetimeoffset-converter.md).

**Target example:** a project with `timeZone: "Europe/Berlin"` querying a daily
trend returns buckets starting at 23:00 UTC (winter) and 22:00 UTC (summer). The
day containing the spring-forward transition contains 23 hours of events and the
autumn day contains 25 â€” and both are labeled as one day.

### Implementation steps

1. **Do not change the storage format.** Timestamps stay UTC ticks; ADR-0006
   explains why, and time-zone display is a presentation concern. Changing
   storage to solve a bucketing problem is the trap this story is built around.
2. Add a nullable `TimeZoneId` column to `Project`, with a migration. Null means
   UTC, preserving every existing result exactly.
3. Validate the id with `TimeZoneInfo.FindSystemTimeZoneById` at write time and
   reject unknown values with 400. Note in the docs that .NET 10 accepts IANA ids
   on Windows via ICU, but that a machine with `DOTNET_SYSTEM_GLOBALIZATION_
   INVARIANT` set has no time zone database at all â€” a real deployment failure
   worth knowing about.
4. Add an overload `TimeBucket.Truncate(DateTimeOffset, TrendInterval,
   TimeZoneInfo)`. Convert to the target zone, truncate there, then convert the
   boundary back to UTC. **Never** add or subtract a fixed offset â€” that is
   correct only until the DST transition.
5. Handle the two pathological local times explicitly:
   - **Skipped** times (spring forward: 02:30 does not exist) â€”
     `TimeZoneInfo.IsInvalidTime` is true. Decide and document what a day
     boundary does when it does not exist, e.g. Lord Howe Island.
   - **Ambiguous** times (autumn back: 01:30 happens twice) â€”
     `TimeZoneInfo.IsAmbiguousTime` is true. Pick the first occurrence and
     document it.
   These two cases are the story. Everything else is plumbing.
6. Fix `Next` too. Advancing a daily bucket is **not** `AddDays(1)` in local
   terms â€” the transition day is 23 or 25 hours. Advance in the local zone and
   convert back, or you will emit a bucket that overlaps its neighbor.
7. Zero-fill via `Range` must produce exactly one bucket per local day across a
   transition â€” no duplicate label, no gap. This is the assertion that catches
   almost every implementation bug.
8. Thread the project's zone through `QueryService`, `BoundedQueryService`,
   `PersonSessionService`, and retention cohort dates. **Deliberately exclude
   `HourlyAlertService`**: its half-open hourly windows and 24-hour catch-up
   watermark are hour-aligned in UTC and mixing zones there would break the
   evaluation watermark. Document the exclusion â€” the
   [capstone](../astradocs/bootcamp/capstone-review.md) already warns that
   similar time labels hide different contracts, and this is that trap.
9. Return the applied zone in every affected response so a consumer can render
   labels correctly, and never rely on the client guessing.
10. Add `TimeZoneBucketTests` as **pure domain tests** with fixed instants:
    Berlin winter and summer boundaries; the 23-hour and 25-hour days; a
    half-hour zone (Asia/Kolkata); a 45-minute zone (Pacific/Chatham); a
    southern-hemisphere zone where DST runs the other way; the Monday-start week
    rule inside a transition week; and null falling back to identical UTC results.
11. Add API tests capturing events at known instants and asserting bucket
    membership either side of a local midnight. Set the project zone, and assert
    that an unset zone reproduces the pre-change output byte-for-byte.
12. Document the field, the validation, the ambiguity policy, and the alert
    exclusion in [api-reference.md](../docs/api-reference.md) and
    [architecture.md](../docs/architecture.md).

### Acceptance and verification

Daily buckets align with local midnight in both DST states. Transition days
contain 23 or 25 hours and appear exactly once. Half-hour and 45-minute zones
work. Projects with no zone produce byte-identical results to before. Hourly
alerts are unchanged.

Run `dotnet test --filter "FullyQualifiedName~TimeZoneBucket"` then
`--filter "FullyQualifiedName~QueryEdgeCaseTests"` then the full suite.

**Common mistake:** storing a fixed UTC offset instead of a zone id. It works
for six months. **Done when:** the 23-hour and 25-hour day assertions pass.
**Explain:** why can you not implement this by adding an offset to the stored
tick value?

---

# Tier 3 â€” Platform

<a id="web-18"></a>
## WEB-18 â€” Count the caller who actually authenticated

**User story:** As an operator, I want the ingestion rate limit applied to the
project that authenticated the request, so one customer cannot consume another's
budget and cannot escape its own.

**Current starting point:** the limiter in
[Program.cs](../src/Pulse.Api/Program.cs) partitions on the `X-Api-Key` header,
falling back to the client IP. `/capture` also accepts the write key in the
**request body** as `api_key`, which the README presents as the primary form.
Two consequences, both verified against the current code:

- Every body-key client shares one partition per source IP. A single busy
  customer behind a NAT exhausts the budget for everyone behind it.
- A caller can send an arbitrary `X-Api-Key` header value â€” it is never
  validated against the body key â€” and land in a fresh, unused partition on
  every request while authenticating normally with the body key.

This is finding Â§5.2 of the [engineering report](engineering-report.md).

**Open:** [Program.cs](../src/Pulse.Api/Program.cs),
[CaptureEndpoints.cs](../src/Pulse.Api/Endpoints/CaptureEndpoints.cs),
[CaptureRequestParser.cs](../src/Pulse.Api/Endpoints/CaptureRequestParser.cs),
[QueueAdmissionService.cs](../src/Pulse.Infrastructure/Services/QueueAdmissionService.cs),
and [ProductionReadinessTests.cs](../tests/Pulse.Tests/Api/ProductionReadinessTests.cs).

**Target example:** two projects each send 300 events in one window; both
succeed. One project sending 301 is throttled while the other is unaffected.
Rotating the `X-Api-Key` header value on every request no longer evades the
limit.

### Implementation steps

1. **Reproduce both problems first**, as failing tests, before changing
   anything. One test drives two projects through the body-key form from one
   client and shows them sharing a budget; another rotates the header value and
   shows the limit never triggering. A fix without a reproduction is a guess.
2. Understand the ordering problem, because it is the whole difficulty: the rate
   limiter runs **before** authentication in the pipeline, but the identity you
   want is only known **after** parsing and validating the body. You cannot
   simply read the project id in the limiter's partition callback.
3. Choose an approach and record why:
   - **(a) Resolve the key early** in a small middleware placed before
     `UseRateLimiter`: parse the header or peek the body, look up the project id,
     stash it in `HttpContext.Items`, and partition on it. Costs a database
     lookup before limiting â€” which is itself a load an attacker can drive.
   - **(b) Partition on a cheap authenticated-ish proxy**: hash the presented
     key (header *or* body) without a database lookup. No lookup, correct
     partitioning per key, and unknown keys get their own partitions â€” which is
     acceptable because they are rejected downstream anyway.
   - **(c) Two-stage**: a coarse IP limit before authentication, plus a precise
     per-project limit as an endpoint filter after it.
   **Recommendation: (b) plus (c).** Hash-partition cheaply for the front line,
   then apply an exact per-project limit after admission. Defense at both layers
   is the standard answer, and (a) alone lets unauthenticated traffic drive
   database load.
4. Peeking the body before the handler requires `EnableBuffering()` and a
   rewind, or the handler will read an empty stream. Cap the peek â€” do not buffer
   a batch of 1,000 events to extract one field. Coordinate with
   `CaptureRequestParser` so parsing happens once, not twice.
5. Normalize the partition key: the same project must map to the same partition
   whether the key arrived in the header or the body. Assert exactly that.
6. Keep a fallback partition for requests presenting no key at all, so
   unauthenticated flooding is still bounded. Its budget should be smaller.
7. Preserve the configured `PermitLimit`/`WindowSeconds` names and defaults.
   Operators may already depend on them.
8. Consider per-project overrides. `GET`/`PUT /api/projects/{id}/ingestion/limits`
   already exists â€” check whether it should feed this limit rather than adding a
   parallel configuration path. Reusing it is very likely the right answer.
9. Add `RateLimitIdentityTests`: two projects are independent; header rotation no
   longer evades; header form and body form share one partition; missing-key
   requests use the fallback; the existing rate-window recovery test still passes;
   and â€” the important one â€” with the fix reverted, the reproductions from step 1
   fail again.
10. Update the [ingestion runbook](../docs/runbooks/ingestion.md) and remove the
    corresponding row from
    [review-findings.md](../docs/learning/review-findings.md), replacing it with
    the evidence.

### Acceptance and verification

Per-project isolation holds under both key-presentation forms. Header rotation
does not evade the limit. Unauthenticated traffic remains bounded. No existing
capture test changes behavior.

Run `dotnet test --filter "FullyQualifiedName~RateLimitIdentity"` then
`~ProductionReadinessTests` then the full suite.

**Common mistake:** moving `UseRateLimiter` after `UseAuthentication` to make
identity available, thereby allowing unauthenticated floods to reach the
authentication handler and its database lookups. **Done when:** both original
reproductions fail. **Explain:** why is rate limiting before authentication, on
a less precise key, the safer default?

---

<a id="web-19"></a>
## WEB-19 â€” Notify a customer's system when an alert fires

**User story:** As a customer, I want Pulse to POST to my endpoint when an
hourly alert triggers, so my on-call tooling reacts without polling.

**Current starting point:** `HourlyAlertService` evaluates rules and writes
`ProjectNotification` rows read through an in-app API. Nothing leaves the
process â€” the application makes **no outbound HTTP calls at all** and registers
no `HttpClient`. This story introduces outbound network I/O, which brings an
entire new threat model with it.

**Depends on WEB-20** for the outbox, if you build both. Build WEB-20 first.

**Open:** [HourlyAlertService.cs](../src/Pulse.Infrastructure/Services/HourlyAlertService.cs),
[HourlyAlertWorker.cs](../src/Pulse.Api/Scheduling/HourlyAlertWorker.cs),
[ExportWorker.cs](../src/Pulse.Api/Export/ExportWorker.cs) for the lease and
ownership pattern to copy,
[lesson 25](../astradocs/bootcamp/25-hourly-alerts.md), and
[lesson 17](../astradocs/bootcamp/17-retries-and-worker-ownership.md).

**Target example:** a project registers `https://hooks.example.com/pulse`. When
an alert fires, Pulse POSTs a JSON body with headers
`X-Pulse-Signature: v1=<hex>`, `X-Pulse-Timestamp: <unix seconds>` and
`X-Pulse-Delivery: <uuid>`. Non-2xx responses retry with exponential backoff for
24 hours, then the endpoint is disabled and an in-app notification says so.

### Implementation steps

1. **Threat-model first and write it down.** This endpoint makes your server
   issue HTTP requests to a URL a customer chooses. That is server-side request
   forgery, and it is the reason this story is in the senior tier. Everything in
   step 3 follows from it.
2. Add `WebhookEndpoint` (project id, url, secret hash, enabled, failure count,
   disabled-at) and `WebhookDelivery` (endpoint id, payload, attempt count, next
   attempt at, status, last response code, owner, attempt generation). Migration
   plus permission-matrix entries at Admin.
3. Defend against SSRF, at **request time, not registration time**:
   - Require `https` and reject any other scheme.
   - Resolve the hostname and reject loopback, link-local (169.254.0.0/16 â€”
     this is the cloud metadata endpoint), private ranges, unique-local IPv6, and
     multicast.
   - Reject IP-literal hosts entirely.
   - **Re-check after DNS resolution and pin the resolved address**, because a
     hostname that resolved publicly at registration can resolve to 169.254.169.254
     at delivery time. That is DNS rebinding, and validating only the URL string
     does not stop it.
   - Disable automatic redirect following. A 302 to `http://169.254.169.254`
     bypasses every check above.
4. Sign the payload: HMAC-SHA256 over `timestamp + "." + body` with a per-endpoint
   secret shown once at creation and stored hashed, matching how personal API
   keys are handled in
   [ApiKeyGenerator](../src/Pulse.Domain/ApiKeyGenerator.cs). Include the
   timestamp in the signed material so a captured delivery cannot be replayed
   later, and document the tolerance window receivers should enforce.
5. Set an aggressive `HttpClient` timeout (10 seconds) via
   `AddHttpClient` with a named client. A customer endpoint that never responds
   must not occupy a worker. Configure the handler lifetime deliberately and note
   why socket exhaustion makes a static `HttpClient` per call the wrong shape.
6. Deliver from a **background worker reading a durable queue**, never inline in
   the alert evaluation. An HTTP call inside the evaluation transaction holds a
   SQLite write lock open across the network â€” with the read-lock behavior
   described in WEB-25, that stalls the whole application. This is the single
   most important design point in the story.
7. Reuse the existing lease-and-fence pattern from the export worker: owner id
   plus attempt generation, checked inside the transaction. Do not invent a new
   ownership mechanism when a tested one exists.
8. Retry with exponential backoff plus jitter. Cap total attempts and total
   elapsed time. Disable the endpoint after the cap and write an in-app
   notification â€” a silently disabled webhook is worse than none.
9. Treat delivery as **at-least-once** and document it. Include a stable
   `X-Pulse-Delivery` id so receivers can deduplicate, and say plainly in the
   docs that receivers must be idempotent. Promising exactly-once here would be
   false.
10. Cap the payload size and never include person properties by default. An
    alert webhook that ships customer PII to an arbitrary URL is a data-exfil
    channel wearing a feature's clothes.
11. Add `WebhookTests` with a stub receiver hosted in the test: successful
    delivery; signature verification with a known vector; a 500 retrying with
    growing delays; the disable threshold; a timeout counting as a failure; SSRF
    rejections for loopback, link-local, private, IP-literal and redirect cases;
    at-least-once behavior under a simulated worker restart; and lease fencing
    proving a stale worker cannot mark a delivery complete.
12. Write `docs/runbooks/webhooks.md`: how to verify a signature (with sample
    code), what the retry schedule is, how to re-enable a disabled endpoint, and
    the at-least-once caveat.

### Acceptance and verification

Deliveries are signed verifiably. Every SSRF vector is rejected, including the
post-resolution and redirect cases. Retries back off and eventually disable.
Restarting mid-delivery never loses or double-completes a delivery record. No
HTTP call happens inside a database transaction.

Run `dotnet test --filter "FullyQualifiedName~Webhook"`.

**Common mistake:** validating the URL string at registration and never again.
**Done when:** the rebinding and redirect tests pass. **Explain:** why is
delivering from a worker rather than inline a correctness requirement here, not
just a performance preference?

---

<a id="web-20"></a>
## WEB-20 â€” Invite a teammate who does not have an account yet

**User story:** As an admin, I want to invite someone by email and have them
receive a link, so onboarding does not require them to register first and tell me
when they are done.

**Current starting point:** `POST /api/projects/{id}/members` takes an `email`
and requires that **the user already exists** â€” the README says so explicitly.
There is no email capability anywhere in the codebase, no password reset, and no
single-use token mechanism. This is the most user-visible gap in the platform.

**Open:** [ProjectEndpoints.cs](../src/Pulse.Api/Endpoints/ProjectEndpoints.cs),
[AuthEndpoints.cs](../src/Pulse.Api/Endpoints/AuthEndpoints.cs),
[ApiKeyGenerator.cs](../src/Pulse.Domain/ApiKeyGenerator.cs) for the
hash-and-show-once pattern,
[PasswordHasher.cs](../src/Pulse.Domain/PasswordHasher.cs), and
[ErasureWorker.cs](../src/Pulse.Api/Lifecycle/ErasureWorker.cs) as a worker to
model the dispatcher on.

**Target example:** `POST /api/projects/{p}/invitations` with
`{"email":"new@example.com","role":"Editor"}` returns 202. An
`OutboundMessage` row is written in the same transaction as the invitation. The
dispatcher sends it. The recipient opens the link, sets a password, and lands as
an Editor. The token works exactly once and expires in 72 hours.

### Implementation steps

1. Build the **transactional outbox** first, before any email concern. Add an
   `OutboundMessage` entity (id, kind, recipient, payload JSON, status, attempts,
   next attempt at, owner, attempt generation, created at). Migration included.
2. Understand why the outbox is the point of the story: writing an invitation row
   and calling an email API are two systems. Commit fails after the send and you
   have emailed a link to a nonexistent invitation; send fails after the commit
   and you have an invitation nobody knows about. **Writing the message row in
   the same transaction as the invitation makes the pair atomic**, and a worker
   turns rows into sends afterwards. This is exactly the durable-queue idea from
   [ADR-0001](../docs/adr/0001-async-ingestion-durable-queue.md) applied to a
   different output â€” say so in the ADR you write.
3. Define `IEmailSender` in `Pulse.Infrastructure` with one method. Ship two
   implementations: `LoggingEmailSender` (writes the rendered message to the log,
   the default, so the platform has no new dependency) and a documented SMTP
   implementation using `System.Net.Mail.SmtpClient` behind configuration. **No
   new package.** State clearly in the docs that the logging sender is not a
   delivery mechanism.
4. Add `ProjectInvitation` (project id, email, role, token hash, expires at,
   accepted at, created by). Store **only the SHA-256 hash** of the token,
   exactly as personal API keys are stored â€” a leaked database must not yield
   working invitation links.
5. Generate the token with `RandomNumberGenerator`, at least 32 bytes. Reuse
   `ApiKeyGenerator`'s approach and extend it rather than writing new crypto.
6. Accepting an invitation must be **atomic and single-use**: a conditional
   update `SET AcceptedAt = @now WHERE Id = @id AND AcceptedAt IS NULL`, checking
   the affected row count, inside the same transaction that creates the
   membership. Two simultaneous accepts must produce one membership. Test it with
   concurrent requests â€” this is the same lesson as WEB-10 in a different place.
7. Compare the token in constant time with
   `CryptographicOperations.FixedTimeEquals`, as `PasswordHasher.Verify` already
   does. Look the record up by a separate non-secret id, not by the secret.
8. Add password reset using the identical machinery â€” a different `kind` on the
   outbox row and a different token purpose. **Bind the purpose into the stored
   record** so an invitation token can never be replayed as a password reset.
   Getting this reuse right, without letting the two token types cross, is the
   real design work.
9. Make reset **non-enumerating**: `POST /api/auth/password-reset` returns 202
   whether or not the address exists. Otherwise the endpoint is a free
   account-existence oracle. Add a test asserting identical responses and
   similar timing for both cases.
10. Rate-limit invitation and reset creation per project and per email. Without
    it, this is an outbound spam relay pointed at addresses an attacker chooses.
11. Write the dispatcher worker modeled on `ErasureWorker`: lease with owner and
    generation, bounded retries with backoff, terminal failure state, and no
    network call inside a transaction.
12. Add `InvitationTests` and `PasswordResetTests`: full accept flow for a new
    user and an existing user; expiry; single-use under concurrency; a wrong
    token; an invitation-token-as-reset attempt rejected; a non-existent email
    producing an identical reset response; the outbox row committing with the
    invitation and not without it; dispatcher retry and terminal failure; and
    revocation before acceptance.
13. Write `docs/runbooks/invitations.md` and an ADR recording the outbox choice
    and the deliberate no-new-dependency default.

### Acceptance and verification

An invitation and its outbox row commit together or not at all. Tokens are
single-use under concurrent acceptance, expire, and cannot cross purposes. Reset
does not reveal whether an address exists. No network call occurs inside a
transaction.

Run `dotnet test --filter "FullyQualifiedName~Invitation"` and
`--filter "FullyQualifiedName~PasswordReset"`.

**Common mistake:** sending the email inside the request handler, before the
transaction commits. **Done when:** a forced commit failure leaves no message and
a forced send failure leaves a retryable row. **Explain:** why is the outbox the
same idea as the ingestion queue, and what does each guarantee that a direct call
cannot?

---

<a id="web-21"></a>
## WEB-21 â€” Watch the ingestion queue drain live

**User story:** As an operator recovering from a backlog, I want a live stream of
queue depth, so I can see progress without polling a metrics endpoint every
second.

**Current starting point:** `GET /api/ingestion/metrics` is polled, and
`TestIngestion.WaitForDrainAsync` polls it every 20 ms â€” the codebase already
demonstrates the polling cost this story addresses. Nothing streams; every
response is a complete buffered body.

**Open:** [IngestionEndpoints.cs](../src/Pulse.Api/Endpoints/IngestionEndpoints.cs),
[IngestionCounters.cs](../src/Pulse.Infrastructure/Services/) (the counters
singleton), [IngestionSignal](../src/Pulse.Infrastructure/Services/) (the
`Channel` used to wake the worker), and
[RequestLoggingMiddleware.cs](../src/Pulse.Api/RequestLoggingMiddleware.cs).

**Target example:** `GET /api/projects/{p}/ingestion/stream` with
`Accept: text/event-stream` returns `200`, `Content-Type: text/event-stream`,
and a sequence of `event: metrics` frames roughly once per second, plus a `:
heartbeat` comment every 15 seconds, until the client disconnects.

### Implementation steps

1. Choose SSE over WebSockets and justify it: the data flows one way, SSE is
   plain HTTP so the existing auth, permission matrix and logging all apply
   unchanged, and browsers reconnect automatically. WebSockets would need a
   parallel authorization path. Record this in the handler comment.
2. Set `Content-Type: text/event-stream`, `Cache-Control: no-cache`, and
   `X-Accel-Buffering: no` â€” the last one because a buffering reverse proxy will
   silently hold your frames and the feature will appear broken in production but
   work locally.
3. Disable response buffering with `IHttpResponseBodyFeature.DisableBuffering()`
   and flush after every frame. Without the flush nothing is delivered until the
   response ends, which for a stream is never.
4. Get the frame format exactly right: `data: <json>\n\n`. The **blank line
   terminates the event** and is the most common mistake. Send `id:` so a client
   can resume with `Last-Event-ID`, and `retry:` to set the reconnect delay.
5. Send a heartbeat comment (`: heartbeat\n\n`) every 15 seconds. Idle
   connections are dropped by proxies and load balancers, and the heartbeat is
   what keeps them alive and detects a dead peer.
6. Honor `HttpContext.RequestAborted` in every loop and every await. A stream
   that ignores cancellation leaks a request thread and a database scope per
   disconnected client â€” the leak that turns this feature into an outage.
7. **Do not query the database in a tight loop.** Read the in-process
   `IngestionCounters` singleton, or subscribe to the existing signal. A
   per-second `COUNT(*)` per connected client, combined with the read-lock
   behavior in WEB-25, is a self-inflicted denial of service.
8. Cap concurrent streams per project and total, returning 429 beyond the cap.
   Long-lived connections are a finite resource and must be budgeted like one.
9. Exclude the route from `RequestLoggingMiddleware`'s duration logging, or every
   stream logs a multi-minute request on disconnect and distorts your latency
   view.
10. Coordinate with WEB-12: response compression over a stream buffers frames and
    breaks delivery. Exclude this path explicitly and test it with compression
    enabled â€” this interaction is the kind of thing that only shows up in
    production.
11. Add the route to `ProjectPermissionMatrix` as Viewer with `analytics:read`.
12. Add `EventStreamTests`: headers correct; at least two well-formed frames
    received; frames parse as JSON; heartbeat appears; the stream ends promptly on
    client cancellation (assert with a timeout); the connection cap returns 429; a
    non-member gets 404 before any frame; compression does not corrupt frames.
13. Document the frame shape, the heartbeat interval, the cap, the proxy
    buffering caveat, and how to consume it with `curl -N`.

### Acceptance and verification

Frames arrive incrementally, not as one buffered body. Cancellation tears down
the connection promptly with no leaked scope. Heartbeats keep idle connections
alive. Caps are enforced. Compression does not corrupt the stream.

Run `dotnet test --filter "FullyQualifiedName~EventStream"`.

**Common mistake:** omitting the terminating blank line, so a client receives
bytes and never fires an event. **Done when:** `curl -N` shows frames appearing
over time. **Explain:** why does a streaming endpoint need explicit cancellation
handling when a normal endpoint does not?

---

<a id="web-22"></a>
## WEB-22 â€” Finish in-flight work during a deploy

**User story:** As an operator, I want a restart to stop accepting new requests,
finish the ones in flight, and let workers reach a safe point, so deploying does
not drop accepted events or abandon a running export.

**Current starting point:** six hosted services run background loops
(ingestion, export, erasure, retention, flag schedule, hourly alerts, capture
metadata cleanup). Shutdown behavior is whatever the default 5-second host
timeout produces. The workers already have durable leases so nothing is *lost* â€”
but a killed worker leaves a lease to expire, which delays recovery on the next
start. Restart correctness is proven by tests; restart *grace* is not.

**Open:** [Program.cs](../src/Pulse.Api/Program.cs),
[IngestionWorker.cs](../src/Pulse.Api/Ingestion/IngestionWorker.cs),
[ExportWorker.cs](../src/Pulse.Api/Export/ExportWorker.cs),
[ExportOwnershipService.cs](../src/Pulse.Infrastructure/Services/ExportOwnershipService.cs),
and [lesson 17](../astradocs/bootcamp/17-retries-and-worker-ownership.md).

**Depends on WEB-04** for the readiness gate.

**Target example:** on `SIGTERM`, `/health/ready` immediately returns 503 while
`/health/live` stays 200. In-flight requests complete. Workers finish the current
item, release their leases, and exit. The process ends within the configured
grace period.

### Implementation steps

1. Set `HostOptions.ShutdownTimeout` explicitly from configuration with a 30-
   second default. The 5-second default is short for a worker mid-batch, and
   leaving it implicit means nobody knows what it is.
2. Flip the WEB-04 readiness gate to not-ready on
   `IHostApplicationLifetime.ApplicationStopping`. Then **wait** a configurable
   drain delay (default 5 seconds) before proceeding: a load balancer needs to
   observe the failing probe before you stop accepting work. Skipping this wait
   is why "graceful" shutdowns still drop requests.
3. Audit every worker loop for cancellation handling. Each must check the token
   between items, not only at the top of the loop, and must not swallow
   `OperationCanceledException` into a retry.
4. **Distinguish the two cancellation moments.** A worker that has claimed an
   item should finish that item and then stop, rather than abandoning it
   mid-transaction. Give each worker a linked token: stop *taking* new work
   immediately, but let the current unit finish under a longer budget. Getting
   this distinction right is the substance of the story.
5. On stop, actively **release leases** rather than letting them expire. The
   ownership services already know how to check owner and generation inside a
   transaction; add an explicit release path using the same fence. This is what
   turns a lease-expiry delay on the next start into an immediate pickup.
6. Never write a lease release without the owner and generation check. A worker
   shutting down must not release a lease another worker has already taken over.
7. Ensure the ingestion `Channel` signal drains rather than blocks: a worker
   awaiting a signal must wake on cancellation, not hang until the host timeout
   kills it.
8. Log a structured shutdown summary per worker â€” items completed, item
   abandoned (if any), leases released, elapsed. Shutdown problems are otherwise
   invisible until they cause a mysterious delay on the next start.
9. Add `GracefulShutdownTests` using `IHostApplicationLifetime.StopApplication()`
   against a factory host: readiness flips before shutdown proceeds; a request in
   flight completes; a worker mid-item finishes it; leases are released rather
   than left to expire; the process stops within the timeout; a second host
   started immediately afterward picks up work without waiting for lease expiry.
   That last assertion is the user-visible payoff.
10. Document the sequence, the two timeouts, and the orchestrator settings that
    must match them (termination grace period must exceed drain delay plus
    shutdown timeout) in [architecture.md](../docs/architecture.md) and a new
    `docs/runbooks/deploys.md`.

### Acceptance and verification

Readiness fails before request acceptance stops. In-flight requests complete.
Workers finish their current item and release leases. A replacement host picks up
immediately rather than waiting out a lease. Shutdown completes inside the
timeout.

Run `dotnet test --filter "FullyQualifiedName~GracefulShutdown"` then the full
suite â€” cancellation changes surface as timing flakiness elsewhere.

**Common mistake:** flipping readiness and shutting down in the same instant, so
the load balancer never observes the change. **Done when:** the immediate-pickup
assertion passes. **Explain:** the workers already recover from a hard kill â€” so
what does graceful shutdown actually buy?

---

<a id="web-23"></a>
## WEB-23 â€” Let a client generate itself from the API description

**User story:** As an API consumer, I want a machine-readable description of
every endpoint, so I can generate a typed client instead of transcribing 757
lines of Markdown by hand.

**Current starting point:** there is no OpenAPI document and no Swagger â€” I
checked; the string appears nowhere in the source.
[api-reference.md](../docs/api-reference.md) is 757 hand-maintained lines
covering 110 routes. It is currently accurate, which is impressive and entirely
unenforced. The interesting half of this story is not generating the document â€”
it is proving the document is true.

**Open:** [Program.cs](../src/Pulse.Api/Program.cs),
[ProjectPermissionMatrix.cs](../src/Pulse.Api/Auth/ProjectPermissionMatrix.cs),
every file in [Contracts/](../src/Pulse.Api/Contracts/), and
[api-reference.md](../docs/api-reference.md).

**Target example:** `GET /openapi/v1.json` returns a valid OpenAPI 3.1 document
describing all 110 routes with schemas, security schemes and error responses.
A test fails the build if a registered route is missing from the document or
absent from the permission matrix.

### Implementation steps

1. Use the built-in `Microsoft.AspNetCore.OpenApi` (`AddOpenApi` / `MapOpenApi`).
   It ships with the framework. Do **not** add Swashbuckle or NSwag; the
   four-package dependency budget is worth defending, and a UI is not required to
   generate a client.
2. Add `.Produces<T>(...)` / `.ProducesValidationProblem()` annotations to
   handlers. There are 110 routes: do this in passes by endpoint module, and
   commit each pass separately. A single sweeping commit is unreviewable.
3. Describe the four security schemes accurately: JWT bearer, personal API key
   (`Authorization: Bearer pk_user_...`), project write key (body `api_key` or
   `X-Api-Key`), and project read key (`X-Api-Key`). The prefix-dispatch policy
   scheme means one header carries two different credential types â€” the document
   must not pretend otherwise.
4. Apply the correct security requirement **per route**, driven by
   `ProjectPermissionMatrix`. A route the matrix marks `AllowsReadKey` must show
   the read key as acceptable; the others must not. Generating this from the
   matrix rather than by hand is what keeps it honest.
5. Add a schema transformer for `DateTimeOffset` fields noting the UTC
   normalization from [ADR-0006](../docs/adr/0006-utc-ticks-datetimeoffset-converter.md).
   A generated client that assumes offsets are preserved will be wrong.
6. Document error responses once as reusable components: RFC 7807 problem
   details, the 404-not-403 membership rule from
   [ADR-0002](../docs/adr/0002-404-not-403-membership.md), and the standard
   401/403/404/422/429 set.
7. **Write the contract tests. This is the deliverable.**
   - Enumerate `EndpointDataSource` and assert every non-`/health` route appears
     in the generated document.
   - Assert every project-scoped route resolves to a `ProjectPermissionMatrix`
     rule. **A route missing from the matrix has no restricted-token enforcement
     and fails open** â€” this test converts a silent authorization hole into a
     build failure, and it is worth the whole story on its own.
   - Assert the document parses as valid OpenAPI.
   - Assert every documented `2xx` names a response schema.
8. Add a check that the committed `docs/openapi.json` matches the generated one,
   so the artifact cannot drift. Commit the generated file and fail the test when
   it differs, with the fix being to regenerate.
9. Decide what happens to `api-reference.md`. Recommendation: keep it as
   narrative â€” the generated document describes shapes, not the 404-not-403 rule
   or the ingestion semantics â€” but stop duplicating per-route field tables and
   link to the generated document instead. Say this in the file.
10. Note the honest limitation: the document describes what the routes are
    annotated to return, not what they actually return. The annotations are
    hand-written and can be wrong. Only the tests make them true.

### Acceptance and verification

Every registered route appears in the document and in the permission matrix,
enforced by enumeration. The document validates. The committed artifact matches
the generated one. A route added without a matrix entry fails the build â€” prove
this by adding one temporarily.

Run `dotnet test --filter "FullyQualifiedName~OpenApiContract"`.

**Common mistake:** generating a document nobody verifies, which drifts within a
month and is then worse than the Markdown it replaced. **Done when:** the
enumeration tests pass and a deliberately unregistered route fails.
**Explain:** why is the permission-matrix coverage test more valuable than the
document itself?

---

<a id="web-24"></a>
## WEB-24 â€” Keep a browser session without exposing a token to scripts

**User story:** As a user of the web console, I want to stay signed in across
refreshes without a token any injected script can read, so an XSS bug does not
hand over my account.

**Current starting point:** `POST /api/auth/login` returns a JWT with an
eight-hour lifetime (`Jwt:LifetimeMinutes: 480`). A browser client must store it
somewhere, and WEB-07 deliberately holds it in a variable and loses it on
refresh. There is no cookie authentication, no refresh mechanism, and **no way
to revoke an issued JWT** â€” it is valid until it expires.

**Depends on WEB-07** (a browser client to secure) and **WEB-08** (origins).

**Open:** [AuthEndpoints.cs](../src/Pulse.Api/Endpoints/AuthEndpoints.cs),
[JwtTokenIssuer.cs](../src/Pulse.Api/Auth/JwtTokenIssuer.cs),
[Program.cs](../src/Pulse.Api/Program.cs) (the policy scheme),
[PersonalApiKeyAuthenticationHandler.cs](../src/Pulse.Api/Auth/PersonalApiKeyAuthenticationHandler.cs)
as the model for a hashed-credential handler, and
[AuthEndpointsTests.cs](../tests/Pulse.Tests/Api/AuthEndpointsTests.cs).

**Target example:** `POST /api/auth/session` sets
`Set-Cookie: pulse_session=...; HttpOnly; Secure; SameSite=Lax; Path=/` plus a
readable `pulse_csrf` cookie. Subsequent same-origin requests authenticate from
the cookie; unsafe methods additionally require the CSRF value in an
`X-CSRF-Token` header. `POST /api/auth/session/refresh` rotates both.
`DELETE /api/auth/session` revokes immediately.

### Implementation steps

1. **Understand the trade you are making, and write it down.** A cookie is
   immune to script theft (`HttpOnly`) but is sent automatically by the browser,
   which creates CSRF exposure that a bearer header does not have. You are
   trading one class of attack for another and mitigating the second. If you
   cannot explain that sentence, do not start this story.
2. Add a `Session` entity: id, user id, token hash, created at, expires at,
   last-seen at, revoked at, user agent hash. Store **only the hash** of the
   session token, exactly as personal API keys are stored. Migration included.
3. Set the session cookie with `HttpOnly = true`, `Secure = true`,
   `SameSite = Lax`, `Path = "/"`, and an explicit expiry. Justify `Lax` over
   `Strict` in a comment (`Strict` breaks inbound links) and over `None`
   (`None` requires `Secure` and re-opens cross-site sending).
4. Implement CSRF as a double-submit pair: a random value in a **non-HttpOnly**
   cookie plus the same value in a request header, compared in constant time.
   The reason this works is the same-origin policy â€” a cross-site attacker can
   cause the cookie to be sent but cannot read it to populate the header. Write
   that reasoning in the file; it is the part people implement without
   understanding.
5. Require the CSRF header on unsafe methods only (POST, PUT, PATCH, DELETE).
   Requiring it on GET breaks ordinary navigation for no benefit.
6. Add the cookie scheme to the existing policy scheme selector in `Program.cs`
   **without disturbing the prefix dispatch**. Precedence must be explicit: an
   `Authorization` header wins over a cookie, so API clients are unaffected. Test
   the case where both are present.
7. Enforce CSRF **only for cookie-authenticated requests**. A bearer-token
   request is not CSRF-able and must not be forced to carry a header â€” otherwise
   every existing client breaks.
8. Rotate the session id on refresh and on privilege change. Keep a short overlap
   so a concurrent in-flight request with the old id does not fail; document the
   overlap window. Session fixation is what rotation prevents.
9. Support revocation properly: `DELETE` marks the row revoked and the
   authentication handler checks it. This is the capability plain JWTs lack, and
   it is a legitimate reason to prefer server-side sessions â€” say so in the ADR.
10. Cache the session lookup per request (`HttpContext.Items`) so one request
    does not hit the database repeatedly, but never cache across requests â€” that
    would defeat revocation.
11. Coordinate with WEB-08: cookie authentication across origins needs
    `AllowCredentials`, which cannot be combined with a wildcard origin. Turn it
    on here, alongside the explicit allowlist, and test that an unlisted origin
    gets nothing.
12. Add `SessionAuthTests`: cookie flags exactly as specified; authenticated
    request via cookie; unsafe method without the CSRF header returns 403; with a
    mismatched value returns 403; with a matching value succeeds; bearer requests
    need no CSRF header; header wins when both are present; revocation takes
    effect on the very next request; rotation invalidates the old id after the
    overlap; an expired session returns 401; and a cross-origin request from an
    unlisted origin is rejected.
13. Write ADR-0012 recording why sessions coexist with JWTs rather than replacing
    them, and update [project-permissions.md](../docs/project-permissions.md)
    with the new credential type.

### Acceptance and verification

Session cookies are unreadable by script and carry all three protective
attributes. CSRF is enforced for cookie auth on unsafe methods only. Revocation
is immediate. Bearer clients are entirely unaffected â€” the full existing auth
suite must pass unchanged.

Run `dotnet test --filter "FullyQualifiedName~SessionAuth"` then `~AuthEndpointsTests`
then `~AuthzMatrixTests`.

**Common mistake:** enforcing CSRF on bearer requests too, breaking every API
client, or comparing the CSRF values with `==`. **Done when:** cookie and bearer
paths coexist with no change to existing clients. **Explain:** why does the
double-submit pattern work at all, and what same-origin property is it relying on?

---

<a id="web-25"></a>
## WEB-25 â€” Refresh ten dashboards at once without queueing

**User story:** As a team of analysts, we want our dashboards to refresh
concurrently, so ten people opening reports at 9 a.m. do not wait in line behind
each other.

**Current starting point:** this is finding Â§5.1 of the
[engineering report](engineering-report.md), and the project already records it
in [review-findings.md](../docs/learning/review-findings.md): *"Protected project
reads take SQLite's reserved writer lock; reads serialize with writers and each
other to enforce erasure pause ordering."*

Concretely, `ProjectReadSnapshotFilter` in
[ProjectMaintenanceAccess.cs](../src/Pulse.Api/Auth/ProjectMaintenanceAccess.cs#L38-L59)
wraps every member-facing `GET`/`POST` read in
`BeginTransaction(deferred: false)`, taking a RESERVED lock. Every protected read
in the process therefore executes one at a time regardless of core count.

**This is the hardest story in the set, and the first half is measurement, not
code. Do not skip to the fix.**

**Open:** [ProjectMaintenanceAccess.cs](../src/Pulse.Api/Auth/ProjectMaintenanceAccess.cs),
[PersonErasureService.cs](../src/Pulse.Infrastructure/Services/PersonErasureService.cs),
[PulseDbContext.cs](../src/Pulse.Infrastructure/PulseDbContext.cs),
[lesson 21](../astradocs/bootcamp/21-person-erasure.md), and
[docs/runbooks/person-erasure.md](../docs/runbooks/person-erasure.md).

**Target example:** twenty concurrent dashboard refreshes on a seeded project
complete in materially less wall-clock time than twenty sequential ones, while
every erasure-ordering test continues to pass unchanged.

### Implementation steps

1. **Establish the invariant before touching anything.** Read the erasure lesson
   and runbook and write, in your own words, exactly what the reserved lock
   guarantees: a protected read must not observe a state that straddles a
   maintenance generation change. Any replacement must preserve that sentence. If
   you cannot state it precisely, you cannot safely change it.
2. **Measure the current behavior.** Add a benchmark â€” a test-hosted harness, not
   a new package â€” that seeds a project with a substantial event volume and
   issues N concurrent reads, reporting p50/p95/max and total wall clock for
   N = 1, 2, 5, 10, 20. Record the numbers in the report. Serialization should be
   visible as latency growing roughly linearly with N. **If it is not, stop:
   the assumption is wrong and that finding is more valuable than the fix.**
3. Confirm the mechanism rather than inferring it. Log lock wait times, or
   instrument the filter with a stopwatch around `BeginTransaction`, and show
   where the time actually goes. Distinguish lock contention from query cost â€”
   they are fixed by different changes.
4. Only now evaluate alternatives, each against the step 1 invariant:
   - **WAL journal mode** (`PRAGMA journal_mode=WAL`) allows concurrent readers
     alongside one writer. Combined with a **deferred** transaction, protected
     reads could stop taking a writer lock entirely. Check whether the erasure
     ordering guarantee survives â€” WAL gives a read a consistent snapshot, which
     may be exactly what the reserved lock was being used to obtain.
   - **Generation check without a lock**: read the maintenance generation,
     perform the read, re-read the generation, and retry if it changed. This is
     optimistic concurrency, the same shape as WEB-10 and the worker fences.
   - **Narrow the scope**: apply the lock only to routes that genuinely read
     person-identifying data, rather than to every Viewer-level GET and POST.
     Note that the filter already exempts `/capture/validate` â€” that exemption is
     a precedent for scoping.
   - **Keep it and document a ceiling.** A measured, documented limit is a
     legitimate outcome, and the honest one if the alternatives cannot preserve
     the invariant.
5. If you adopt WAL, enable it at connection open and understand what changes:
   a `-wal` sidecar file appears (backup procedures must copy it), checkpointing
   behavior changes, and WAL does not work over network filesystems. Update the
   [database upgrade runbook](../docs/runbooks/database-upgrades.md) â€” a backup
   procedure that copies only the main file is now silently incomplete, which is
   a data-loss bug introduced by a performance change.
6. Whatever you choose, **every existing erasure test must pass unchanged**.
   `PersonErasureWorkflowTests` and the read-serialization tests encode the
   invariant. If you find yourself editing an erasure assertion to make a
   performance change pass, stop â€” you are changing the guarantee, not the
   implementation.
7. Add a test that directly targets the ordering hazard: begin a read, start an
   erasure that changes the maintenance generation, complete the read, and assert
   the read did not observe a mixed state. Run it repeatedly; a race that
   reproduces one time in fifty is still a bug.
8. Re-measure with the same harness and publish before/after numbers. A
   performance change without a measured delta is a hypothesis.
9. Update [review-findings.md](../docs/learning/review-findings.md): either move
   the row to "addressed" with the evidence, or replace it with the measured
   ceiling and the reason the alternatives were rejected. **Both are successful
   outcomes for this story.**

### Acceptance and verification

The invariant from step 1 holds, proven by the existing erasure suite plus the
new ordering test. Before/after measurements are recorded with methodology and
conditions. If WAL is adopted, backup documentation is updated in the same change.

Run the full suite, then `--filter "FullyQualifiedName~PersonErasure"`, then the
benchmark harness. Report conditions honestly â€” the existing
[verification record](../astradocs/bootcamp/verification.md) discloses processor
count and sampling for its timings, and yours must too.

**Common mistake:** removing the lock, observing that the tests pass, and
declaring victory â€” the erasure tests may not exercise the exact interleaving the
lock prevents, which is why step 7 exists. **Done when:** the numbers are
published and the invariant is demonstrably intact. **Explain:** what precisely
does the reserved lock guarantee, and does a WAL snapshot give you the same thing
or something subtly different?

---

# Coverage map

What each story adds relative to the existing 75, and what it deliberately
leaves out.

| Topic | Stories | Previously covered? |
| --- | --- | --- |
| Response security headers, CSP | WEB-01, WEB-07 | No |
| Rate-limit contract and identity | WEB-02, WEB-18 | Limiting existed; contract and identity did not |
| Request correlation | WEB-03 | Ingestion tracing only |
| Probe semantics | WEB-04, WEB-22 | Single combined probe |
| Hypermedia pagination | WEB-05 | Offset and cursor paging existed; links did not |
| Media types and negotiation | WEB-06, WEB-12 | No |
| Browser client | WEB-07, WEB-24 | Offline lesson page only |
| CORS | WEB-08 | No |
| HTTP caching and validators | WEB-09, WEB-13 | No |
| Optimistic concurrency at HTTP level | WEB-10 | Worker fences only |
| Idempotency beyond ingestion | WEB-11 | Capture admission keys only |
| Range requests | WEB-13 | No |
| File upload and CSV parsing | WEB-14 | CSV writing only |
| Versioning and deprecation | WEB-15 | No |
| Partial success | WEB-16 | One batch route |
| Time zones and DST | WEB-17 | UTC only |
| Outbound HTTP, SSRF, signing | WEB-19 | No outbound calls at all |
| Email, outbox, single-use tokens | WEB-20 | No |
| Streaming responses | WEB-21 | No |
| Graceful shutdown | WEB-22 | Crash recovery only |
| API description and contract tests | WEB-23 | Hand-written Markdown only |
| Cookies, CSRF, revocation | WEB-24 | Bearer tokens only |
| Read concurrency measurement | WEB-25 | Documented as a known limitation |

**Still not covered by these 25**, and worth a future set: WebSockets and
bidirectional protocols; OpenTelemetry export and distributed tracing across
services; CDN and shared-cache behavior; content addressing and object storage;
multi-tenancy beyond projects; SSO/OIDC and SCIM; usage metering and billing;
i18n of error messages; and any horizontal-scaling concern, all of which are
blocked on leaving SQLite.

# Suggested order

Dependencies are real. Building in roughly this order avoids rework:

1. **WEB-01, WEB-03, WEB-04** â€” independent, small, and every later story
   benefits from the headers, the correlation id and the readiness gate.
2. **WEB-06 then WEB-09** â€” media types before caching, because `Vary` handling
   must be right before anything depends on it.
3. **WEB-05, WEB-02, WEB-16** â€” independent contract work.
4. **WEB-10, WEB-11** â€” concurrency and idempotency; WEB-10 needs WEB-09's
   validator helper.
5. **WEB-07 then WEB-08** â€” build the page same-origin, then make it work
   cross-origin.
6. **WEB-12, WEB-13, WEB-14, WEB-15** â€” independent; WEB-12 needs WEB-06 and
   WEB-13 needs WEB-09.
7. **WEB-17** â€” self-contained but demanding; do it when you have a clear day.
8. **WEB-18** â€” fixes a real finding; do it before anything else touches the
   limiter.
9. **WEB-20 then WEB-19** â€” build the outbox for email, then reuse the delivery
   pattern for webhooks.
10. **WEB-22, WEB-21** â€” shutdown before streaming, because a stream that
    ignores shutdown is how you discover you needed WEB-22.
11. **WEB-23** â€” after the routes have stopped moving, so the document is worth
    generating.
12. **WEB-24** â€” needs WEB-07 and WEB-08.
13. **WEB-25** â€” last. It is measurement-led, touches the most sensitive
    invariant in the system, and every other story adds load that makes the
    measurement more meaningful.

# A closing note on evidence

Every story above ends with an acceptance section because the existing bootcamp
established a standard worth keeping: a story is not done when the code compiles,
and it is not done when a test passes â€” it is done when the evidence is recorded
and the **remaining limitation is named**.

Each of these 25 has one. WEB-09 still builds the response it does not send.
WEB-11 deduplicates only within a retention window. WEB-13 saves the client
bandwidth and the server nothing. WEB-19 delivers at-least-once and no better.
WEB-25 may well conclude that the current design is correct and the ceiling
should simply be documented.

Write those down when you finish. The limitation you name is worth more to the
next reader than the feature you shipped.

