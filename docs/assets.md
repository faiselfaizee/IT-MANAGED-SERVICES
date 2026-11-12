# Assets module (`assets`)

First product module on the platform. Licence module key `assets`, limit `max_assets` (active assets, i.e. not retired or lost). Everyone signed in can look; administrators change. Needs the module in the customer's licence (402 `module_not_licensed` otherwise).

## What it holds
- **Asset types** (Laptop, Monitor, Phone ...), each with its own extra fields: `text`, `number`, `date`, `bool`, `choice` (with options). Up to 30 per type.
- **Assets**: tag (`AST-000001`, or your own), name, type, status (`stock`, `in_use`, `repair`, `retired`, `lost`), manufacturer, model, serial (unique), location, assigned to, purchase date and cost, warranty end, notes, the type's extra fields, source (`manual`, `import`; `agent` later).
- **History** per asset: who changed what, from and to. Also written to the platform audit log.

## API
| | |
|---|---|
| `GET /api/assets/types`, `POST`, `PUT /{id}`, `DELETE /{id}` | types (delete refused while assets use it) |
| `GET /api/assets?search=&status=&typeId=&assignedTo=&take=&skip=` | list, newest first, `{total, items}` |
| `GET /api/assets/{id}` | asset and history |
| `POST /api/assets`, `PUT /api/assets/{id}`, `DELETE /api/assets/{id}` | create, change (only the fields you send), delete |
| `GET /api/assets/summary` | counts by status and type, warranty ending in 30 / 90 days, expired |
| `GET /api/assets/export.csv` | all assets (cells that start with `= + - @` get a leading `'` so a spreadsheet cannot run them) |
| `POST /api/assets/import[?createTypes=true]` | CSV body, header line required with a `name` column; columns: tag, name, type, status, manufacturer, model, serial, location, assigned_to, purchase_date, purchase_cost, warranty_end, notes. Answers `{created, skipped, errors:[{line,error}]}` |

## Phase 2: device management (all of the old Asset app)

| Area | What you get | Where |
|---|---|---|
| Companies | customers with a per-customer "remote access allowed" switch (off by default) | `#/assets/companies`, `/api/assets/companies` |
| AMC and rentals | contracts with period, coverage, visits, SLA and the assets they cover; an asset shows "AMC" or "Rented" | `/api/assets/amc`, `/api/assets/rentals` |
| Asset page | Overview, Storage, Network, Service checks, Applications, Tickets, Logs, Commands, Shell, PowerShell ISE, Remote control | `#/assets/{id}` |
| Software inventory | every program across all computers, drill down to the computers, CSV export | `/api/assets/software` |
| Agents | enrolment tokens (`dkat_...`), install scripts for Windows and Linux, check-in, long poll, auto-created assets, re-install recognised by serial number and MAC | `/agent/v1/*`, `/api/assets/enrolments`, `/api/assets/agents` |
| Command repository | 71 built-in troubleshooting commands (editable), parameters, risk level; run with output, cancel, history | `/api/assets/commands`, `/api/assets/{id}/run` |
| Elevated shell and script editor | live cmd / PowerShell / bash on the device; every input line is audited; closes after 30 minutes idle | `/api/assets/{id}/shell` |
| Remote control | RustDesk set-up (attended or unattended), connect link, progress log | `/api/assets/{id}/remote-control` |
| Discover | probe by IP or name, scan up to a /22, fetch details with an agent or a saved credential (WinRM, SSH), add as asset | `/api/assets/discover/*` |
| Site collectors | a service inside a customer network that scans, pings, runs service checks and reads details; found devices are adopted into assets | `/collector/v1/*`, `/api/assets/collectors` |
| Service checks | TCP port, web page, certificate expiry, DNS; results kept per check (history for Monitoring) | `/api/assets/{id}/checks` |
| QR labels | per asset (compact, full, or link), A4 sheet of 100 or single labels | `/api/assets/qr/*` |
| Allocate and hand over | who holds the asset, condition, accessories, history | `/api/assets/{id}/allocate`, `/handover` |
| Remote activity | everything run remotely, all devices | `/api/assets/remote-log` |

### Remote access guardrails
A command, shell or remote-control session only starts when **all** of these are true: the signed-in person is an administrator; the customer allows it; the asset allows it; the agent is not "monitor only"; the agent is online. Disruptive commands need an explicit confirmation, command parameters must match a safe pattern, at most 3 shells are open per device, and everything is written to the audit log (including shell input).

### Ticket links (Service Desk not built yet)
"Raise ticket" and the Tickets tab keep a link record on the asset (`assets.ticket_links`). When the Service Desk module exists, its tickets replace these and the links move across.

### Configuration
| Setting | Meaning |
|---|---|
| `Assets:AgentDir` | folder with `version.txt` and the signed agent files (`deskask-agent-windows-amd64.exe`, `deskask-agent-linux-amd64`, `deskask-agent-linux-arm64`, each with `.sig`). The old suite's signed binaries work with this server. |
| `Assets:PublicUrl` | address agents and collectors use to reach this server (defaults to the request address) |
| `Assets:AgentOfflineSeconds` | how long without a check-in counts as offline (default 900) |
| `Assets:SecretKey` | key for sealed secrets (falls back to `Identity:EncryptionSecret`) |
| `Identity:Clients:0:Id` = `web`, `RedirectUris:0` = `https://<your address>/auth/callback` | lets the web app sign in |

WinRM discovery of Windows computers needs the platform server to run on Windows; on Linux use the agent. SSH discovery needs `ssh` on the server.


## General agent and site controller (collector)

One program, `deskask-agent`, does both jobs. The platform serves it from `agent-dist` (filled by every release).

**General agent** (Windows, Linux; desktops, laptops, servers). Reports CPU (model, sockets, cores, threads), RAM, disks (type, size, partitions, file system, free space), network cards (up/down, IP, MAC), installed applications with versions, OS, serial, antivirus and patches on Windows. Runs jobs, self-updates and rolls back if the new build never checks in.

- Install on one machine: Assets > Enrolments > install script (`install.ps1` / `install.sh`), or `deskask-agent install --server URL --token dkat_...` as admin/root.
- Check what it would report without enrolling: `deskask-agent inventory`.

**Site controller.** Installed on one VM per customer network: `deskask-agent collector-install --server URL --token dkco_...`. It pings, scans (up to /22), reads SNMP v2c from printers, switches and firewalls, runs TCP/HTTP/SSL/DNS checks, and pushes the agent to machines.

- Logins stay on the controller only: `deskask-agent collector-cred add --kind windows|ssh|snmp ...` (see `deskask-agent help`).
- Push install: Linux over SSH (key or password), Windows over PowerShell remoting (WinRM). A Windows target needs the controller to run on Windows. The target downloads the agent itself using a one-day token.
- No credentials or WinRM? Run the same `install.ps1` line from a Group Policy startup script or an Intune script, using an enrolment token.

**Not included yet:** live shell and remote-desktop messages from the server, SNMP v3, ONVIF or vendor-specific biometric and CCTV reading (those are found by scan and ping/port checks only), macOS.

## Saved logins for the controller
Save the logins under **Assets > Credentials** (Windows user and password, Linux SSH password or private key, SNMP community). They are sealed in the customer's database and the platform sends them only to a controller, only with the work for a computer of that customer (or to every customer when the login has none). A login saved on the controller itself with `collector-cred` is tried first. When no Windows login exists the controller uses its own account, which is rarely enough; the log then says so and says what to fix (WinRM not enabled, port 5985 closed, TrustedHosts, refused login).

## Who may edit and delete assets
- Administrators and engineers add and edit assets. Viewers only read.
- Only people with the **delete** right can delete: administrators, and anyone (or any group, such as "Team leads" or "Managers") given the Assets > delete grant under Access.
- Everyone else uses **Request deletion** on the asset and gives a reason. The request appears under **Assets > Delete requests**; an approver approves (the asset is deleted) or rejects with a note. A person cannot approve their own request (administrators excepted). Every step is in the audit log.
