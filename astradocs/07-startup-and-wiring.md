# How the app starts and gets its helpers

**Keep one sentence:** `Program.cs` assembles the application; endpoint and
worker code use the services registered there.

## Startup as a checklist

Follow [Program.cs](../src/Pulse.Api/Program.cs) from top to bottom:

1. Create the application builder and read configuration.
2. Register the database context, services, workers, and authentication.
3. Build the application.
4. Inspect the configured database and refuse startup unless its checked-in
   migration history and structure are current.
5. If running with `seed`, generate demo data and exit.
6. Configure request middleware and map endpoints.
7. Call `app.Run()` to run the server and its hosted services.

Startup is verify-only: it neither creates a missing database nor applies a
pending migration. Stop writers and use the explicit `db status`,
`db adopt-legacy`, and `db upgrade` commands as indicated. Fresh databases and
subsequent schema changes are built from checked-in EF migrations. Follow the
[database-upgrade runbook](../docs/runbooks/database-upgrades.md) for backup,
legacy adoption, upgrade, verification, and recovery steps.

## What "dependency injection" means here

Suppose the capture endpoint needs a `CaptureService`. Its handler declares
that parameter. The application framework supplies an instance using the
registrations in `Program.cs` and resolves its constructor's dependencies.

```csharp
builder.Services.AddScoped<CaptureService>();
builder.Services.AddSingleton<IngestionSignal>();
```

Read the first line as "Make CaptureService available within a service scope."
Read the second as "Share one ingestion signal instance through this app."

For an HTTP request, a scope supplies related services. The long-running
worker creates its own scope when processing a batch. It does not keep one
request's database context forever.

The registration is a recipe for supplying an object. It does not immediately
call `IngestAsync`, capture an event, or open every database query.

## The request passes through middleware

The explicit middleware calls include exception handling, status pages,
request logging, rate limiting, authentication, and authorization before
endpoint work. Read them in their actual source order.

Middleware is work around request handling. For example,
[RequestLoggingMiddleware](../src/Pulse.Api/RequestLoggingMiddleware.cs)
times a request and writes a log after calling the next part of the pipeline.

Project membership is still checked by `ProjectAccessService` in endpoint
handlers; global authentication alone does not establish that membership.

**Repeat using the story:** Program hires the helpers; the endpoint asks for
their work; a scope decides which helper instances belong together. That is
an analogy for object lifetimes, not an operating-system process per helper.

**Find and explain:** who supplies `IdentityService` to `CaptureService`?
Where does `IngestionWorker` create a scope?

Next: [the objects those services work with](08-people-and-tables.md).
