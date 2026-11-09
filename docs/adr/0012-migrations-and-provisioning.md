# ADR 0012: Versioned migrations and customer provisioning

Status: accepted (9 Nov 2026). Completes ADR 0002 and ADR 0003.

## Decision
- Every module lists its database changes as numbered `Migration` steps. `SchemaMigrator` applies the missing ones in order, each in a transaction, under one PostgreSQL advisory lock, and records name and checksum in `meta.migrations`. Editing an applied step is refused.
- Version 1 of each module creates only what is missing, so databases made before this change are adopted without harm. Identity's version 1 is the schema generated from the first release's model; from version 2 on, every change is plain SQL.
- The catalog (`platform.tenants`, `platform.licenses`) is migrated the same way (module "catalog"). `deploy/catalog.sql` is no longer needed.
- Customers are created from the command line of the host itself (`tenant create`), which reuses the real module registrations. Create is resumable and switches the customer on last.
- No web API for creating customers yet. It needs a platform-operator identity that is separate from any customer's administrators; that is a later decision.

## Consequences
- Rolling out a version: update the files, run `tenant migrate`, restart. Failures show per customer.
- Rolling back code does not roll back the database. Migrations must be written so the previous version still works with them (add, do not remove or rename in the same release).
