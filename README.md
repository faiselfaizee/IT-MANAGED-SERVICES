# DeskAsk Platform (working name)

One platform for Service Desk, Assets, Monitoring, AD Manager, Vault and Backup. Rebuild of the DeskAsk Suite 1.46.0 (see docs/adr).

| Folder | What | Built and tested with |
|---|---|---|
| `src/Platform` | .NET 10 modular monolith: Abstractions, Tenancy, Host | `dotnet build DeskAsk.Platform.slnx` |
| `tests` | xUnit tests (tenancy, host) | `dotnet test DeskAsk.Platform.slnx` |
| `web` | React and TypeScript shell | `npm ci && npm run build && npm test` |
| `agent` | Go agent (outbound heartbeat) | `go vet ./... && go test ./...` |
| `deploy` | Local PostgreSQL and MinIO, catalog schema | `docker compose -f deploy/docker-compose.yml up -d` |

## Run locally
1. `docker compose -f deploy/docker-compose.yml up -d`
2. `dotnet run --project src/Platform/DeskAsk.Platform.Host --urls http://localhost:5000` (with `Platform__CatalogConnection="Host=localhost;Database=deskask_platform;Username=postgres;Password=dev"`; without it an in-memory "dev" customer is used)
3. `cd web && npm ci && npm run dev`, open http://localhost:5173

## Phase 0 status
Done: repository layout, ADRs, tenancy (host name to customer), host with roles, web shell, agent heartbeat, CI.
Next (Phase 0 gate): identity (OpenIddict), audit, licensing/entitlements, settings, jobs, the Connect gateway endpoint the agent calls, signed bundles.
Note: the .NET part was written without a .NET SDK at hand; the first `dotnet build` is the real test. The web and agent parts are built and tested.
