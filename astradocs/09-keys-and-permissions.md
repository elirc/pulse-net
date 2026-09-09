# Keys and permissions, slowly

**Keep one sentence:** knowing who a caller is and deciding what they may
access are separate checks.

## First: identify the caller

A JWT or personal API key can identify a management user. This is
**authentication**. Read that word as "Who is making this request?"

A project membership check determines whether that user may access the
requested project. This is **authorization**. Read it as "May this caller
do this here?"

## Then: match the credential to the job

| Credential | Used where | Plain explanation |
| --- | --- | --- |
| JWT from login/register | `Authorization: Bearer ...` | Management session identifying a user |
| `pk_user_` personal key | `Authorization: Bearer ...` | Script-friendly identity with that user's project access |
| `pk_live_` project write key | Body `api_key` or supported header | Submit events and evaluate flags through `/decide` |
| `rk_live_` project read key | `X-Api-Key` on supported routes | Read supported project query/evaluation data |

A name containing "write" does not mean unlimited write access. A project
write key cannot manage project membership or replay dead letters.

The [demo script](../scripts/learning-demo.ps1) uses a management token to
create a project, its write key to capture, and its read key to query.

## Three short stories

**Mara has a valid token and belongs to project A.** She can request A's
member-only ingestion metrics.

**Noah has a valid token but is not a member of A.** He gets `404` for A's
member-only project routes. His login succeeded; the project access did not.

**A caller supplies A's read key to replay a letter.** The route requires
membership through a management identity, so this credential is insufficient.

## One extra check beginners often miss

Even if Mara belongs to projects A and B, A's replay route must not replay
B's letter. Read `ReplayAsync` in
[IngestionOperationsService](../src/Pulse.Infrastructure/Services/IngestionOperationsService.cs).
The letter lookup checks both `projectId` and `letterId`.

Say it twice: "Check access to the project. Then fetch the child record
inside that project." A valid child ID alone does not establish the right scope.

## Interpret these statuses in this codebase

| Status | Example meaning here |
| --- | --- |
| 400 | Capture input failed validation |
| 401 | Required credentials missing or invalid |
| 404 | Resource absent, already consumed, or hidden by project-access policy |
| 202 | Capture or replay durably accepted queued work |
| 422 | Replay rejected unsuitable stored event data |

**Check:** does a successful login prove Mara can read every project?

<details>
<summary>Answer</summary>

No. Login establishes identity. `ProjectAccessService` checks project access.
Find its `RequireMemberAsync` method and the membership query.

</details>

Next: [the other features built around the same data](10-feature-tour.md).
