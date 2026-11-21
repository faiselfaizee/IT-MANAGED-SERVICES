# DeskAsk documentation

These pages cover installing, setting up and fixing DeskAsk. Each page answers one kind of question. Headings name the task or the symptom, so a person or a future help assistant can search for the words they see on screen.

## Start here

| I want to… | Read |
|---|---|
| Install DeskAsk on a customer's own server (on-prem), from start to finish | [install-onprem.md](install-onprem.md) |
| Run the vendor platform (SaaS, platform.deskask.in) and add customers | [install-saas.md](install-saas.md) |
| Look up a setting in `config\deskask.json` | [configuration.md](configuration.md) |
| Fix something that does not work | [troubleshooting.md](troubleshooting.md) |

## Reference, by subject

| Subject | Page |
|---|---|
| Windows installer, setup page, IIS | [windows-install.md](windows-install.md) |
| Creating and managing customers (`tenant` command) | [provisioning.md](provisioning.md) |
| Releases, updates, the updater service | [updates.md](updates.md) |
| Licences, modules, limits, activation, single-company mode | [licensing.md](licensing.md) |
| Vendor console, price list, store, trials | [store.md](store.md) |
| Platform ↔ website link | [website-link.md](website-link.md) |
| Branding, logo, report design, own web address | [branding.md](branding.md) |
| Custom domains on the proxy | [custom-domains.md](custom-domains.md) |
| Microsoft, Google and Active Directory sign-in | [sign-in.md](sign-in.md) |
| Security of an installation | [security.md](security.md), [iso27001.md](iso27001.md) |
| Assets, agents, site controllers, discovery, RustDesk | [assets.md](assets.md) |
| Monitoring, alert rules, Teams, syslog and traps | [monitoring.md](monitoring.md) |
| Service Desk, knowledge base, ratings | [servicedesk.md](servicedesk.md) |
| AD Manager | [admanager.md](admanager.md) |
| Vault | [vault.md](vault.md) |
| Backup | [backup.md](backup.md) |
| Design decisions | [adr/](adr/) |

The website (www.deskask.in) has its own guide in the `deskask-website` repository: `deploy/README.md` (install, update, backup, troubleshooting) and `deploy/RELEASE-NOTES.md`.

## Words used in these pages

- **Customer** (also **tenant**): one organisation on DeskAsk, with its own database `deskask_<id>` and its own address. On-prem there is usually one.
- **Company**: a client organisation inside a customer (an MSP has many; single-company mode has exactly one).
- **Catalog**: the database listing customers, their addresses and licences (`Platform:CatalogConnection`).
- **Vendor platform**: DamazeTech's own installation at platform.deskask.in. It issues licences and runs the store.
- **Agent**: `deskask-agent` on a computer or server. **Site controller** (collector): the same program in collector mode, one per customer network. It scans, pushes agents, reads devices, and talks to domain controllers.
- **Install folder**: `C:\DeskAsk` by default (`-Root`). Configuration is `config\deskask.json`; logs are in `logs\`.

## Writing these pages

- Name a heading after the task ("Moving to a new server") or the exact message on screen ("This server only accepts HTTPS requests").
- For a problem, write **Symptom**, **Cause**, **Fix**, and how to **Check** it worked.
- Give commands in full, ready to copy. Say where to run them (which server, PowerShell as Administrator).
- When a release changes behaviour, update the page and mention the version (for example "from 6.14.0").
