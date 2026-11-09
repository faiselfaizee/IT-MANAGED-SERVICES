# Phase 0 gate review (9 Nov 2026)

Question for the gate: is the foundation good enough to build the product modules (HelpDesk, Service Desk, Assets, Monitoring, AD, Vault, Backup) on?

## What exists and is tested (71 backend tests, 12-check end-to-end run, Go tests, release dry run; all in CI on every push)
| Area | State | ADR |
|---|---|---|
| One platform, modules plug in (`IPlatformModule`), roles web / worker / connect | done | 0001, 0004 |
| One database per customer, customer found by host name, schemas prepared per customer | done | 0002, 0003 |
| Sign-in: OpenIddict, code flow + PKCE, lockout, two-step (TOTP + recovery codes), per-customer keys, rotation, JWKS | done | 0006, 0011 |
| Tamper-evident audit log, settings, daily audit check | done | |
| Agent link (Connect): agent tokens, heartbeat, follows server interval | done | 0007 |
| Licences: signed, module gating (402), limits, grace period | done | 0008 |
| Signed releases, updater with rollback, upload from the platform | done | 0009 |
| Background jobs: queue, retries, recurring, admin API | done | 0010 |
| CI: backend on PostgreSQL, web, agent, updater, end-to-end, release dry run | done | |

## Not done yet (known, accepted for Phase 0)
- **Windows Server install has not been run on a real server.** CI proves the Linux path and the bundles for both. An `install-windows.ps1` and a test on a clean Windows Server is the first job of Phase 1 or when you install.
- **Database changes are create scripts, not migrations.** Fine while there is no production data. Before the first real customer: real migrations per module.
- **Web app is a shell** (sign-in, health, layout). No module screens yet.
- **Customer provisioning** (creating a customer, its database, its first admin) is by hand/config. A provisioning tool/API is needed before SaaS onboarding.
- **Data-protection keys** go to disk only if `Platform:DataProtectionPath` is set. Set it in production.
- **Production secrets** are yours to create: `RELEASE_SIGNING_KEY` (GitHub), `Identity:EncryptionSecret` (server). No test covers a wrong secret at start-up beyond the sealed-key check.
- **No load or security test** beyond unit/integration tests. Plan one before go-live.

## Recommendation
Pass the gate. Nothing above blocks module work. Build Phase 1 in this order: (1) customer provisioning + real migrations, (2) Windows install script and a clean-server test, (3) first product module on top (suggested: Assets, because Monitoring, Service Desk and Backup all reference it).
