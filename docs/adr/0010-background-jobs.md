# ADR 0010: Background jobs

Status: accepted

## Decision
- Each customer database has `jobs.queue` and `jobs.recurring`. No separate broker: the database we already run per customer is the queue.
- A job is claimed with `FOR UPDATE SKIP LOCKED` and a lease (`Jobs:LeaseSeconds`, default 900). Several workers can run side by side and never take a job twice. If a worker dies, the job is taken again once its lease ends.
- A failing job is retried after 10 s, 20 s, 40 s ... up to 1 h, and is marked `failed` after `Jobs:MaxAttempts` (default 5). An admin can retry a failed job (`POST /api/platform/jobs/{id}/retry`, audited).
- A dedupe key keeps a second copy of a waiting or running job from being queued.
- Recurring jobs: whoever moves `next_run` forward owns that round, so two workers never queue the same one.
- The worker runs only when `Platform:Roles` includes `worker`. It walks the active customers one by one; one customer's failure never stops the others.
- Handlers must be safe to run twice. Job payloads are never shown in the admin API (they may hold personal data).

## Jobs shipped now
`core.audit_verify` (daily, writes `audit.verify_failed` if the hash chain is broken), `identity.prune` (daily, old tokens and authorizations), `jobs.cleanup` (daily, old finished jobs).

## Consequences
Product modules add an `IJobHandler` and, if needed, a `RecurringJob`. If one customer's volume ever outgrows Postgres as a queue, the `IJobQueue` interface lets us swap the backend without touching modules.
