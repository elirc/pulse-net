# Person erasure

Admin-only POST `/api/projects/P/persons/U/erasure` and the existing DELETE `/api/projects/P/persons/U` return 202 with a status URL. Both initiate the same durable workflow. They invalidate **all exports in that project**, including completed results and snapshot inputs. A 202 response is acceptance, not completion.

Before use, configure a dedicated random secret of at least 32 bytes as Base64 in `Erasure:Keys:1` and set `Erasure:CurrentKeyVersion` to `1` using the deployment's secret configuration. Do not put production keys in this repository. There is no default application key. Preserve every version referenced by suppression records; losing an old version prevents reliable matching and causes startup/runtime checks to fail closed. A new current version does not replace old fingerprints.

Poll GET `/api/projects/P/erasure-jobs/J`. Status is `pending`, `running`, `needsReview`, `failed`, or `completed`; phase and removal counters show progress. During cleanup, project data operations return 503 `project_maintenance`. Ingestion and export workers cannot publish data into the paused project. Admin job inspection and ingestion status remain available.

For `needsReview`, inspect the metadata-only unreadable list. POST `/api/projects/P/erasure-jobs/J/discard-unreadable` with `items`, each containing exactly one `queueSequence` or `deadLetterId`, plus the observed `contentHash`. This explicitly deletes those unreadable items; at most 100 may be selected. Every selection is validated before any deletion. A changed or unknown item returns 409 and keeps the pause. No endpoint guesses whether an unreadable envelope is safe to retain.

For `failed`, investigate the recorded phase and server failure before POST `/api/projects/P/erasure-jobs/J/resume`. Resume requires current Admin authority and all required suppression keys. Each batch rolls back its data and progress together. Do not manually unpause a failed job: completion verifies inventory before changing availability.

Recovery and review commands also verify that the job still owns the project's active pause and maintenance generation. A 409 `erasure_maintenance_ownership_changed` means the durable gate was changed outside the workflow; restore the gate from authoritative operational evidence before retrying. Do not force a resume or discard against a different job's pause.

`Erasure:WorkerEnabled=false` stops automatic cleanup but leaves initiated projects paused. Reenable it to continue pending/running jobs; failed jobs require explicit resume. The worker processes at most ten jobs per cycle, one bounded phase batch each. Large projects need multiple cycles.

Known aliases remain suppressed afterward, including the anonymous alias in identify events. Capture/replay return 422 `identity_suppressed` for a known match. Processing receipts may show `suppressed`; previously processed events retain their outcome but lose erased event references with retirement reason `erasure`. Unknown aliases cannot be inferred to be the same person. This workflow does not erase external downloads, backups, or unrelated systems and makes no legal compliance claim.

Learn the reasoning and failure experiments in [bootcamp lesson 21](../../astradocs/bootcamp/21-person-erasure.md).
