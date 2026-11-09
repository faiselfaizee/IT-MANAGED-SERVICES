# Architecture decisions

| # | Decision | Status |
|---|----------|--------|
| 0001 | Modular monolith in .NET 10, one repository | accepted |
| 0002 | A database per customer, one schema per module, a platform catalog database | accepted |
| 0003 | React and TypeScript front end, generated OpenAPI client | accepted |
| 0004 | One build, three roles (web, worker, connect); SaaS on Linux, on-prem as Windows services | accepted |
| 0005 | Go agent, one outbound TLS connection, plugins | accepted |
| 0006 | Identity with OpenIddict inside the app | accepted |
| 0007 | Private GitHub, GitHub Actions to build, test and sign | accepted |
| 0008 | Signed licences and entitlements | accepted |
| 0009 | Signed release bundles and a separate updater | accepted |

All seven were confirmed by the owner on 8 Nov 2026 (the recommended option in each case). The reasons are in the proposal document "DeskAsk Platform Rebuild Proposal".
Rule for changing one: write a new ADR that supersedes it; never edit history.
- [0010](0010-background-jobs.md) Background jobs
- [0011](0011-per-customer-keys.md) Per-customer signing and encryption keys
- [0012](0012-migrations-and-provisioning.md) Versioned migrations and customer provisioning
