# pulse-net

A PostHog/Mixpanel-style product-analytics platform — C#/.NET backend only.

## What it does

- **Projects & API keys** — multi-project workspace; every project gets a write key for ingestion.
- **Event ingestion** — `POST /capture` accepts single events or batches, validated by API key.
- **Persons & identity** — anonymous distinct IDs, `$identify` merging, person properties (`$set` / `$set_once`).
- **Analytics queries** — trends (event counts with interval bucketing), funnels (ordered step conversion), retention (day-N return), and saved insights.

## Stack

- .NET 10 / ASP.NET Core Web API
- EF Core + SQLite
- xUnit (unit + `WebApplicationFactory` integration tests)

## Layout

| Project | Purpose |
| --- | --- |
| `Pulse.Domain` | Entities and domain logic, no dependencies |
| `Pulse.Infrastructure` | EF Core `DbContext`, persistence |
| `Pulse.Api` | HTTP endpoints |
| `Pulse.Tests` | Unit + integration tests |

## Getting started

```bash
dotnet build
dotnet test
dotnet run --project src/Pulse.Api
```
