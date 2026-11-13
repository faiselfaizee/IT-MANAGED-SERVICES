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

## Windows push: "WinRM is running but the install fails"
A running WinRM service is not the same as remoting being ready. Before it starts PowerShell, the collector tries the computer's port 5985 (and 5986 for HTTPS) and the log says which one answered. Check, from the collector's computer: `Test-NetConnection <computer> -Port 5985` and `Test-WSMan <computer>`. On the target, as administrator:
- `Enable-PSRemoting -Force` creates the listener and the firewall rule (it refuses on a Public network profile: change the profile or add `-SkipNetworkProfileCheck`).
- The default firewall rule only lets the same subnet in. For a collector on another subnet: `Set-NetFirewallRule -Name WINRM-HTTP-In-TCP -RemoteAddress Any`.
- `winrm enumerate winrm/config/listener` must list a listener.
- The login must belong to the target's Administrators group; use `DOMAIN\user` for a domain account and `.\user` for a local one.
- Outside a domain, the collector's computer must trust the target: `Set-Item WSMan:\localhost\Client\TrustedHosts -Value '<computer>' -Force`.

## Updating the controller
From v2.8.4 on the controller updates itself: when the server holds a newer `agent-dist` build than the controller reports, the controller downloads it over its own connection, checks that it starts, swaps it and restarts; it goes back to the old program if the new one never checks in. A controller installed before that (v2.7.0 or v2.8.x) has to be upgraded once by hand: Assets > Agents & collectors > the controller's install commands > "Upgrade an existing install". The version shown on that page tells which build it runs.

## "The WinRM client cannot process the request"
This is the TrustedHosts error. Windows only uses Kerberos when it connects by computer name inside a domain; connecting by IP address, or to a computer outside the domain, needs the target in the connecting computer's TrustedHosts list (or HTTPS on port 5986). From v2.8.6 the controller adds the target to its own TrustedHosts list and tries again (it runs as SYSTEM). Discover > Fetch details runs on the DeskAsk server, which first tries the computer name found by reverse DNS; if that fails, run on the DeskAsk server as administrator:

    Set-Item WSMan:\localhost\Client\TrustedHosts -Value '192.168.31.*' -Concatenate -Force

For a local account on the target, enter the user as `COMPUTERNAME\Administrator`, and on the target allow remote admin rights for local accounts (`winrm quickconfig` does this with LocalAccountTokenFilterPolicy).

## Installing the agent through a controller: instant start, live log, automatic install
**Start and response time.** The controller (v2.9.0 and later) holds a request open on the platform (`GET /collector/v1/wait`, up to 25 seconds). Pressing *Install agent*, *Scan now* or *Install agents on all devices* answers that request at once, so work starts within a second instead of at the next one-minute check-in. Installs and scans run in the background, so one slow device does not hold up the others (four installs run at the same time), and the result goes back the moment an install ends. A controller older than v2.9.0 keeps working on the one-minute rhythm and updates itself to the new one.

**Live log.** On the device page (Overview > Agent & remote access), *Install agent through a collector* opens a log that fills in while it runs. Each line is one step the controller really did:
queued by whom; whether the controller is online (and when it was last seen if not); handed to the controller; the computer type; the number of logins available; the name or address tried; the WinRM port check (5985, then 5986); which saved login is used; whether the controller adds the target to TrustedHosts; start of the remote command, then every line the target prints (download, enrolment, service start) and a "still working" line every 15 seconds; and finally *Finished* or *Failed* with the reason. The last line is green when the agent has enrolled with the platform. If nothing appears after "Queued", the controller is offline or not picking up work; the second line says which. A device cannot be queued twice while an install is running; after a failure *Try again* clears the log. An install the controller never reports back is marked failed after 20 minutes (30 if it was never picked up).

**Automatic install.** Assets > Agents & collectors > Manage on a controller > *Install the agent automatically on Windows and Linux computers this controller finds*. With it on, each scan adds every new computer as an asset (customer and monitoring default of the controller) and queues the agent install, if a saved login of the right kind exists for that customer (Windows login for computers with WinRM or SMB ports open; SSH password or key for computers with port 22 open). Printers, cameras and everything else stay in the *Found by scans* list. Without a login nothing is added and the controller's activity list says so. The licence limit for assets is respected.

## RustDesk set-up with a live log
Settings > Remote control holds the RustDesk server, relay, public key, installer address and SHA-256. *Set up unattended / attended* on the device's Remote control tab sends the settings to the agent (v2.9.0 and later; earlier agents ignored the message). The agent then, reporting every step to the *Set-up progress* log:
1. downloads the installer over HTTPS with progress and checks the SHA-256 (a mismatch stops the set-up and deletes the file; an empty SHA-256 is allowed but warned about),
2. runs it with `--silent-install`,
3. starts the RustDesk service,
4. sets the servers with RustDesk's own `--option` commands and checks that its settings file holds them (otherwise it writes `RustDesk2.toml` itself),
5. sets the permanent password (unattended mode; the password is never written to the log),
6. waits for the ID and reports it. The *Connect* button then works.
If it fails the log ends with the reason (download blocked, hash mismatch, installer error, service not starting, no ID because the computer cannot reach the RustDesk server on ports 21115 to 21117). *Remove* uninstalls RustDesk the same way. Windows computers only. The steps were tested against a simulated computer; the first real run is the real test.

## Exporting everything about an asset (v2.9.1)

The old **Export CSV** on the asset list only wrote the front-page columns. There are now three fuller exports.

| Where | Button | You get |
|---|---|---|
| Assets list | **Export all details (CSV)** | One flat CSV for every asset you may see |
| Asset page | **Export all details (CSV)** | The same file for that one asset |
| Asset page | **Export PDF** | A document for that one asset, ready to print or send to the customer |

Both cover the same sections: **Identity** (tag, name, status, type, category, company, make, model, serial, BIOS, processor make, model and socket, CPU count, cores, logical processors, RAM, storage), **System** (host name, DNS name, IP, domain, operating system, antivirus, last patch, uptime), **Ownership and contracts** (location, person, purchase date and cost, warranty, AMC, rental, notes, custom fields, who created and changed the record), **Agent and remote access**, **Storage** (disks and partitions), **Network adapters**, **Connected IP addresses**, **Installed applications**, **Tickets**, **Service checks** and the **Activity log** (the latest 500 entries).

The CSV is in long form so Excel can filter it: `asset_tag, asset_name, company, section, row, field, value`. Filter the *section* column to see, for example, every installed application of one customer. Cells that start with `=`, `+`, `-` or `@` are defused so a program name cannot run as a formula. The PDF shows at most 300 rows per table and says so when it cuts; the CSV has all of them. The PDF uses a built-in Latin font, so letters outside Latin show as `?`.

An asset list export covers up to 2,000 assets. People limited to some customers only get those customers. Each export is written to the audit log.

## Credentials by customer and hardware category (v2.9.1)

**Assets > Credentials** now keeps every login under a **customer** and a **hardware category**, and the list is grouped by customer.

- Categories: any hardware, computers and laptops, physical servers, VMware and Nutanix, firewalls, switches, access points and controllers, printers, other.
- Each login also has a **vendor or product** (HPE iLO, Dell iDRAC, Lenovo XClarity, IBM IMM, VMware, Nutanix, Fortinet FortiGate, Cisco, Meraki, HPE Aruba, Ubiquiti, Sophos, SonicWall, Palo Alto) and a free note.
- New kinds: server management (iLO, iDRAC, XClarity, IMM), VMware vCenter or ESXi, Nutanix Prism, network device by SSH, and network device by API token (the user name is optional for a token).
- Picking a category suggests the matching kind. Filter the list by customer and by category.

These logins are only stored for now. The controller does not receive them yet; the hardware readers that use them (servers through Redfish, VMware and Nutanix, then network devices) arrive one family per release, so each can be tested on your own equipment.

### RustDesk set-up stays on "working…" and nothing starts on the computer

Only agents of version **2.9.0 or newer** can run the RustDesk set-up. An older agent ignores the request without a word, so the page keeps waiting and nothing appears in Task Manager. From v2.9.2 the server refuses to start the set-up for an older agent and says which version it found; the RustDesk page also shows the agent version, and warns while no agent has answered. The agent updates itself within a minute or two when the server holds a newer build (the `.dkb` update carries it). If the agent is 2.9.0 or newer, the first line in the log is "The agent (version …) picked up the request"; if that line is missing, the agent is offline or old, and if the log stops after the download line, send me the log.

## v2.9.3: RustDesk install fix, agents restart themselves after an update, agent state everywhere

**RustDesk "WaitDelay expired before I/O complete".** RustDesk's silent installer starts `rustdesk.exe` and ends; the new program kept the installer's output open, so the agent waited for it and reported a failure although the install had worked. The agent now writes the installer output to a file and no longer waits for programs the installer leaves running.

**Agents that only updated after a restart.** After an update the agent ends so that the new program starts. On Windows it runs as a scheduled task, and Task Scheduler's "restart on failure" only covers a task that fails to *start*, not a program that ends; so the agent stayed stopped until the computer restarted. Now:
- the agent asks Windows to start it again about ten seconds after it ends, and is back within about 15 seconds;
- the scheduled task also has a five-minute watchdog trigger, so a stopped agent or controller starts again by itself;
- agents and controllers installed before 2.9.3 add the watchdog to their own task the first time 2.9.3 runs.

Agents still on 2.8.6 that are stopped will start at the next computer restart (or run `schtasks /Run /TN DeskAskAgent` as administrator on that computer), then update themselves to 2.9.3; from then on they restart on their own.

**Agent state on the asset list.** The Status column (in use, stock…) is replaced by **Agent**: Online or Offline, the version, and *update due* when the server holds a newer agent; "No agent" otherwise. The list can be filtered by agent state.

**Update agent from the asset page.** The agent card shows the version and, when an update is due, **Update agent now** with a live log. It also retries a version the agent tried before and put back. Agents from 2.9.3 report every step (download, start check, swap, restart); an older agent only shows up again with the new version, which closes the log. An offline agent cannot be asked; the page says so.

**Home page.** Each product has rings: Assets shows Agents (live, live with update due, not reporting, no agent), Compliance (antivirus on and patches from the last 30 days; not compliant; no data yet) and Warranty; Service Desk shows open tickets, service level (on time, overdue) and closed tickets. Every count is also written beside its ring in one aligned column, and the agent rows open the asset list filtered to that state. Planned products keep their tiles until they exist.

## Reading VMware ESXi and vCenter (v2.10.0)

The site controller reads ESXi hosts and vCenter through the vSphere web service (`https://<host>/sdk`, port 443). The same works for a standalone ESXi host and for vCenter, ESXi 6.5 to 8.

**Set up**
1. On the host, create a read-only account. In the ESXi Host Client: *Manage > Security & users > Users > Add user*, then *Host > Actions > Permissions*, give the user the **Read-only** role. For vCenter, create the user in vCenter and give it **Read-only** at the top level (propagate to children).
2. **Assets > Credentials > Add credential**: the host's customer, hardware category **VMware and Nutanix**, used for **VMware vCenter or ESXi**, user name and password. The customer's own logins are tried before shared ones, and only VMware logins are sent for VMware devices.
3. Make sure the controller reaches the host on port 443.
4. Add the host as an asset (from the scan, by IP is fine). On its **Overview**, in **Read by a controller**, choose the controller and **VMware ESXi or vCenter**, then **Save**. The controller reads it within seconds; **Read now** reads again at any time. After that it is read every six hours.

**What is read**
- **ESXi host:** vendor, model, serial number or service tag, BIOS version and date, processor model, sockets, cores and threads, memory, local disks (SSD or HDD, size), physical network ports with link state and speed, ESXi version and build, uptime.
- **Hardware tab:** power supplies with their state, every hardware sensor (fans, temperatures, voltages, power use; problems first), graphics cards (display-class PCI devices, e.g. NVIDIA), datastores with size and free space, and the virtual machines (power state, guest OS, IP, CPUs, memory, disk used; templates left out).
- **vCenter:** vCenter's version, a table of its hosts (hardware, serial, processor, memory, ESXi version, state and health), and all datastores and virtual machines.
- Everything also goes into the full CSV export and the asset PDF.

**When it fails** the reason is written to the asset's Logs tab, for example "the VMware login was refused", "did not answer like VMware ESXi or vCenter (HTTP 404); check the address and that port 443 reaches the host", or "this login sees no host: give it the Read-only role".

**Limits.** The controller accepts the host's self-signed certificate (it talks to it on the customer's own network). What ESXi reports for power supplies depends on the server vendor's drivers; reading the server's iLO or iDRAC (next release) fills the gaps. Not tested on a real ESXi host yet, only against a simulated one.
