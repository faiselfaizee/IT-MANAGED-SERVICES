# DeskAsk Platform (working name)

One platform for Service Desk, Assets, Monitoring, AD Manager, Vault and Backup. Rebuild of the DeskAsk Suite 1.46.0 (see docs/adr).

| Folder | What | Built and tested with |
|---|---|---|
| `src/Platform` | .NET 10 modular monolith: Abstractions, Tenancy, Host | `dotnet build DeskAsk.Platform.slnx` |
| `tests` | xUnit tests (tenancy, host) | `dotnet test DeskAsk.Platform.slnx` |
| `web` | React and TypeScript shell | `npm ci && npm run build && npm test` |
| `agent` | Go agent (outbound heartbeat) | `go vet ./... && go test ./...` |
| `updater` | Release tool and updater service (Go) | `cd updater && go vet ./... && go test ./...` |
| `deploy` | Local PostgreSQL and MinIO, catalog schema | `docker compose -f deploy/docker-compose.yml up -d` |

## Run locally
1. `docker compose -f deploy/docker-compose.yml up -d`
2. `dotnet run --project src/Platform/DeskAsk.Platform.Host --urls http://localhost:5000` (with `Platform__CatalogConnection="Host=localhost;Database=deskask_platform;Username=postgres;Password=dev"`; without it an in-memory "dev" customer is used)
3. `cd web && npm ci && npm run dev`, open http://localhost:5173

## Phase 0 status
Done: repository layout, ADRs, tenancy (host name to customer), host with roles, web shell, agent heartbeat, CI, identity (OpenIddict: code flow + PKCE, users and clients in each customer database, sign-in page, userinfo, tested end to end against PostgreSQL in CI).
Also done: audit log (hash chain), settings, Connect gateway (agent tokens, heartbeat), signed licences with module gating (ADR 0008).
Also done: two-step sign-in (TOTP, recovery codes, admin reset, `security.require_2fa`), end-to-end CI job with the real agent.
Also done: signed release bundles, the `deskask-updater` service with rollback, upload from the platform (two-step required), release workflow and a release dry run in CI (docs/updates.md, ADR 0009).
Also done: background jobs (per-customer queue, worker role, retries, recurring audit-verify / token-prune / cleanup jobs, admin API; ADR 0010).
Next (Phase 0 gate): per-customer signing keys, then the gate review.

Two-step notes: set `Identity:EncryptionSecret` in production (it seals the authenticator secrets); data-protection keys must be kept on persistent storage in production so the pending-code cookie survives restarts.

Identity notes: the issuer is the customer's web address and ends with "/" (OpenIddict default). Signing key: `Identity:SigningKeyPem` (EC P-256 PEM) plus `Identity:EncryptionSecret`; without them keys are made new at every start (development only). Clients come from `Identity:Clients:N:*` configuration for now. First admin: `Platform:BootstrapAdmin:Email/Password`.
Note: the .NET part was written without a .NET SDK at hand; the first `dotnet build` is the real test. The web and agent parts are built and tested.
