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
2. **Assets > Credentials > Add credential**: the host's customer, hardware category **Hypervisors**, used for **VMware vCenter or ESXi**, user name and password. The customer's own logins are tried before shared ones, and only VMware logins are sent for VMware devices.
3. Make sure the controller reaches the host on port 443.
4. Add the host as an asset (from the scan, by IP is fine). On its **Overview**, in **Read by a controller**, choose the controller and **VMware ESXi or vCenter**, then **Save**. The controller reads it within seconds; **Read now** reads again at any time. After that it is read every six hours.

**What is read**
- **ESXi host:** vendor, model, serial number or service tag, BIOS version and date, processor model, sockets, cores and threads, memory, local disks (SSD or HDD, size), physical network ports with link state and speed, ESXi version and build, uptime.
- **Hardware tab:** power supplies with their state, every hardware sensor (fans, temperatures, voltages, power use; problems first), graphics cards (display-class PCI devices, e.g. NVIDIA), datastores with size and free space, and the virtual machines (power state, guest OS, IP, CPUs, memory, disk used; templates left out).
- **vCenter:** vCenter's version, a table of its hosts (hardware, serial, processor, memory, ESXi version, state and health), and all datastores and virtual machines.
- Everything also goes into the full CSV export and the asset PDF.

**When it fails** the reason is written to the asset's Logs tab, for example "the VMware login was refused", "did not answer like VMware ESXi or vCenter (HTTP 404); check the address and that port 443 reaches the host", or "this login sees no host: give it the Read-only role".

**Limits.** The controller accepts the host's self-signed certificate (it talks to it on the customer's own network). What ESXi reports for power supplies depends on the server vendor's drivers; reading the server's iLO or iDRAC (next release) fills the gaps. Not tested on a real ESXi host yet, only against a simulated one.

## Reading physical servers: iLO, iDRAC, XClarity, IMM (v2.11.0)

The controller reads a server's management controller through **Redfish** (`https://<address>/redfish/v1`, port 443): HPE iLO 4 (firmware 2.30 or later), iLO 5 and 6; Dell iDRAC 8 (2.40 or later) and 9; Lenovo XClarity Controller; IBM/Lenovo IMM2 with Redfish firmware.

**Logins.** **Assets > Credentials > Add credential**: the customer, hardware category **Physical servers**, used for **Server management (iLO, iDRAC, XClarity, IMM)**. A read-only account is enough (iLO: *Login* privilege only; iDRAC: *Read Only* role; XClarity: *Read-only*). Logins filed under Physical servers are tried first; another login is tried only when the controller refuses one.

**Two ways to use it**
1. **The management address is its own asset.** Add the iLO or iDRAC address (from the scan is fine) and on its Overview choose **Server management (iLO, iDRAC, XClarity)** under *Read by a controller*. The asset gets the server's identity and every table below.
2. **A server you already have** (an ESXi host read as VMware, a Windows or Linux server with the agent): on its Overview, enter the **Management address** in *Read by a controller*, pick the controller and Save. The controller reads the management controller as well; its tables are added to the same asset, and identity fields the main read left empty (serial number, BIOS, memory) are filled in. Nothing is duplicated. For an agent computer leave *Read as* on "Not watched (the agent reports)".

**What is read**
- Identity: maker, model, serial number, BIOS version, processor model, sockets, cores and threads, memory.
- **Processors** per socket (model, cores, threads, maximum speed, state).
- **Memory modules** per slot (size, type, speed, maker, part number, serial, state; empty slots left out).
- **Physical drives** behind the RAID controller (model, SSD or HDD, protocol, size, serial, state) and the **storage controllers** with firmware.
- **Power supplies** (model, serial, rated and current watts, input voltage, firmware, state), **fans**, **temperatures** and **power use**; problems are listed first.
- **Graphics cards** (from the processor list and the PCIe devices, shown once).
- Network ports with link state, speed and MAC; the management controller's model and firmware.

Everything shows on the **Hardware** tab and goes into the full export and the PDF. A failed read is written to the Logs tab, for example "the management controller refused the login", "has no Redfish service (/redfish/v1): is this an iLO, iDRAC or XClarity address, and is Redfish enabled?" or "cannot reach ... on port 443". Tested against simulated iDRAC and iLO answers only.

## Other hypervisors: Nutanix, Proxmox VE, XCP-ng, Hyper-V, KVM (v2.12.0)

All of them fill the same **Hardware** tab as VMware: hosts, storage and virtual machines, plus the export and the PDF. Logins go under **Assets > Credentials** with the hardware category **Hypervisors** (formerly "VMware and Nutanix"); each read mode receives only its own kind of login, the customer's own first.

| Product | How it is read | Read as | Login (Used for) |
|---|---|---|---|
| **Nutanix** | Prism Element REST API v2, port 9440 (use the cluster address) | Nutanix (Prism Element) | Nutanix Prism Element; a *Viewer* account is enough |
| **Proxmox VE** | Proxmox API, port 8006 | Proxmox VE (API) | Proxmox VE: an **API token** (user `audit@pve!deskask`, password = token secret, role *PVEAuditor* on `/`) or a user and password (`root` means `root@pam`) |
| **XCP-ng / Citrix Hypervisor** | XAPI on port 443 (use the **pool master**) | XCP-ng or Citrix Hypervisor | XCP-ng or Citrix Hypervisor; a read-only role is enough |
| **Microsoft Hyper-V** | the agent on the host, or the controller over WinRM | (agent) or Windows or Hyper-V host | Windows login, as for any Windows computer |
| **KVM / libvirt**, Proxmox without API | the agent on the host, or the controller over SSH (`virsh`, `qm`, `pct`) | (agent) or Linux, KVM or Proxmox host | Linux login |

**What is read**
- **Nutanix:** cluster name and AOS version; each node with block model, serial, block serial, processor, sockets, cores, memory, hypervisor version, address, **IPMI address** and state; storage containers with free space; VMs with power, host, IPs, vCPUs, memory and disk. A single-node cluster fills the asset's identity.
- **Proxmox VE:** version; each node's processor, sockets, cores, threads and memory; **physical disks** with model, serial, size, type and SMART health; storage (local and shared); **VMs and containers** (marked) with power, node, CPUs, memory and disk; templates left out. A single node fills the asset's identity and network ports.
- **XCP-ng:** each host's maker, model, serial, BIOS, processor and memory; storage repositories; VMs with power, host, IPs, guest OS, vCPUs and memory (templates, snapshots and the control domain left out).
- **Hyper-V:** every VM with power, IPs, CPUs, memory, disk used and generation, beside the host's normal inventory. Nothing to set up when the agent is installed.
- **KVM / libvirt:** every domain with state, CPUs, memory and IP (when libvirt knows it). **Proxmox through the agent:** `qm list` and `pct list` (name, state, memory, disk).

For the physical side of any of these hosts (power supplies, memory modules, drives), add its **Management address** (iLO, iDRAC, XClarity) as described above. Tested against simulated answers only.

## Network devices: firewalls, switches, access points (v2.13.0)

Choose **Network device: firewall, switch, access point (SNMP and configuration)** under *Read as* on the device's Overview. The controller reads it every six hours and on **Read now**.

**Logins** (Assets > Credentials, the device's customer; categories Firewalls, Switches or Access points are tried first):
- an **SNMP community** (used for Printer, switch or firewall (SNMP)) - for hardware and ports;
- a **Network device (SSH user and password)** login - for the configuration backup. Write the **vendor** (Cisco, Fortinet, Aruba, ...) when the device does not say its maker over SNMP;
- or a **Network device (API token or key)** login with the vendor **Fortinet** (REST API token of a read-only API admin), **Palo Alto** (API key) or **Cisco Meraki** (Dashboard API key; the controller needs internet access to api.meraki.com). API logins are tried before SSH.

**What is read over SNMP (all makers)**
- Maker, model, serial number and firmware (ENTITY-MIB; Fortinet, SonicWall, Palo Alto and UniFi also from their own MIBs).
- **Every port**: name, description, kind (Ethernet, fibre, wireless, LAG), state (in use, free, switched off), speed and MAC; and a summary such as "52 ports, 30 in use, 20 free, 2 switched off; in use: 28 at 1 Gbps, 2 at 10 Gbps". VLAN, loopback and tunnel interfaces are left out.
- **Hardware modules** (chassis, stack members, line cards) with model, serial and firmware.
- **Power supplies, fans and temperatures** with their state: Cisco (CISCO-ENVMON-MIB), HP/Aruba (HP-ICF-CHASSIS), Fortinet (hardware sensors). Other makers list the parts without a state.

**Configuration backup**
| Maker | How | Commands / call |
|---|---|---|
| Cisco IOS, IOS-XE, NX-OS | SSH | `terminal length 0`, `show running-config` |
| Fortinet FortiGate | API token, or SSH | `/api/v2/monitor/system/config/backup?scope=global`; SSH: `config system console / set output standard / end`, `show` |
| HP / Aruba (ArubaOS-Switch, AOS-CX) | SSH | `no page`, `show running-config` |
| Palo Alto PAN-OS | XML API (key, or the SSH user and password) | `type=export&category=configuration` |
| SonicWall SonicOS 7 | SSH | `no cli pager session`, `show current-config` |
| Ubiquiti EdgeOS / EdgeSwitch | SSH | `show configuration commands` / `show running-config` |
| Cisco Meraki (MS, MX, MR) | Dashboard API | device, switch ports and their status, VLANs, firewall rules or SSIDs (as JSON); a Meraki switch's ports also fill the ports table |
| Juniper, MikroTik | SSH | `show configuration \| display set \| no-more`, `/export` |
| Sophos (SFOS) | not possible over SSH (the console is a menu) | use Sophos Central or the firewall's scheduled backup; hardware and ports still come over SNMP |

The **Configuration** tab shows the latest configuration, every kept version (a new one only when it changed, the latest 30), **what changed** from the version before, and a download. Configurations contain password hashes and keys: only administrators can see them, each view and download is in the audit log, and they never go into the CSV export or the PDF. A failed backup shows its reason on the tab; the device's other details are still read.

Tested against simulated devices only (an SNMP switch and firewall, a FortiGate and Palo Alto API, the Meraki Dashboard, and a stand-in SSH device).

## Dashboard, agents at scale, SNMP v3 and the patch report (v2.14.0)

### The Assets dashboard in three levels
1. **Customer tiles** (Assets › Dashboard). Each customer has one tile with a small ring of its agents (live, update due, not reporting, no agent) and five figures: live, not reporting, not compliant, warranty expired or ending, and hardware not OK. The first tile, "All customers", totals everything. Tiles with problems come first. You can also sort by name or by number of assets, search by name, and show 60 more at a time. Someone who may see only one customer goes straight to that customer.
2. **A customer's dashboard** (click a tile). It shows six rings: agents, compliance, warranty, asset types, hardware health and network ports. The breadcrumb reads *Assets › Customer*.
3. **The asset list** (click a part of a ring). It opens filtered to that customer and that ring part, for example *Brightway Schools › Not compliant*. Each filter shows as a chip; remove it with ✕. The rings and the list use the same conditions, so the count on a ring is the number of rows the list shows. Counts are for assets in service (not retired or lost). **Export list** and **Export all details** export what the list shows, filters included.

The rings sweep round and the numbers count up when a dashboard opens and when you come back to it. When figures change, they move from the old value to the new one. Nothing moves when the computer is set to reduce motion.

### Agents at scale
The Agents & collectors page no longer lists every agent, because that list grows too slow with thousands of computers. It shows four counts instead: live, live with an update due, not reporting, and no agent. Each count opens the matching assets.

In the asset list, an administrator can tick assets (or choose **all N matching**, up to 5,000) and then:
- **Update agents**: assets that have no agent, are offline, are already up to date or already updating are skipped, and the reasons are counted.
- **Move agents to bin**: the agent stops reporting and the asset stays. You can restore the agent for 30 days.
- **Uninstall agents**: the agent is sent the order to remove itself. Fix in this version: until now, the agent record was deleted at once, which also deleted the queued order, so the agent never got it. The record now stays, marked as being uninstalled. Such an agent may only fetch its order and can no longer report. The record goes once the order is delivered, or after 30 days if the computer never comes back. This also fixes the single Uninstall button.

### Site controllers in the recycle bin
Deleting a site controller now moves it to the recycle bin. It stops working at once, and the devices it watched are freed. **Restore** (within 30 days) brings it back and gives it its devices again. **Delete for good** removes it.

### SNMP v3
Under Credentials, choose **Printer, switch or firewall (SNMP v3 user)**. Enter the user name, the authentication protocol (MD5, SHA or SHA-256) and its password, and the privacy protocol (AES-128, DES or none) and its password. Passwords are at least 8 characters, are stored sealed and are never shown again. When you edit the login, leaving a password empty keeps the saved one. The site controller tries the customer's SNMP v3 logins before community strings, for both network devices and SNMP-read printers. Clear error messages say which part is wrong: unknown user, wrong authentication password, wrong privacy password, or the device clock out of step.

### Patch report
Assets › Patch report lists every computer with an agent, oldest patch first. For each it shows the last patch date, days since that patch, antivirus state and compliance (compliant means antivirus is on and a patch was installed in the last 30 days). You can filter by customer and show all computers, those 30+ days behind, or those not compliant. **Export CSV** saves the report, and the export is recorded in the audit log.

### Deletion approvals by mail
When someone asks to delete an asset, an email goes to everyone who may approve the deletion for that customer (not the person asking and not customer users). The email gives the reason and a link to Assets › Delete requests. This needs mail to be set up under Settings › Mail.

## Locations, vendors and renewal reminders (v3.3.0)

### Locations
**Assets › Locations** holds each customer's locations in three levels:
- **Country**
- **State or region**, inside a country
- **Site**, with a site code, address, city, postal code and site contact

A site sits in a state or directly in a country. That suits countries without states, such as the UAE. Names are unique inside their parent, and site codes are unique per customer.

Each row shows its number of sites and assets, counting everything below it. Clicking the count opens the asset list filtered to that place, and a country or state includes every site under it.

A place cannot be deleted while it holds other places or assets. For a site with assets, **Delete** offers to move the assets to another site first. You can also mark a site inactive: it keeps its assets but is no longer offered for new ones.

**Who may change locations:**
- administrators, for every customer
- a customer administrator, for their own company
- anyone granted *modify* on Assets, for the customers in that grant (engineers by default)

Viewers only read.

On an asset, **Site** (country › state › site, only that customer's) replaces the free-text location. The old **Location** field stays as **Room or area** (for example "2nd floor, server room"). If an asset moves to another customer, a site or customer-specific vendor of the old customer is removed from it.

### Vendors
**Assets › Vendors** lists the suppliers that assets and AMC contracts are bought from.

| Field | Notes |
|---|---|
| Name, code, address (street, city, state, postal code, country) | |
| Government registrations | GSTIN (15 characters, checked; it must contain the PAN), PAN, TAN, VAT/TRN, CIN, and any number of other registrations (MSME/Udyam, IEC, EIN…) |
| Contact | Person, email, phone, website, payment terms |
| Renewal reminders | Whether the vendor is mailed, and to which address (empty: the contact email) |

A vendor is either **shared** (your own supplier, for every customer; only administrators change these) or **for one customer** (managed like that customer's locations).

The vendor page shows:
- the details and registrations
- every asset bought from the vendor, with customer, site, purchase date, PO, invoice, cost and warranty
- the vendor's AMC contracts, renewals included

### Purchase details on an asset
The add and edit forms now have:
- the vendor
- the purchase order number and invoice number
- the purchase date and **warranty period** (1 to 5 years, or any number of months)

The warranty end date is worked out from the purchase date and the period (36 months from 15 Jan 2024 ends on 14 Jan 2027), and can still be typed by hand.

The list can be filtered by place (`?place=`) and vendor (`?vendor=`). Search also finds site names, site codes, PO numbers and invoice numbers.

The CSV export adds `country, state, site, site_code, vendor, po_number, invoice_number, warranty_months`. The CSV import accepts these columns:
- `company` (or `customer`), by name or code
- `site_code`, which needs `company`
- `vendor`, by name; the customer's own vendor is used before a shared one of the same name
- `po_number`, `invoice_number`, `warranty_months`

### AMC contracts
An AMC now has a vendor, a contract number and a cost. **Renew** creates the next contract with the same customer, vendor, cover and assets. It starts the day after the old one ends and runs a year unless you change the dates; a new contract number and cost can be given. The two are linked. The old contract shows "renewed" and no longer sends reminders.

### Reminders: one month and 15 days before
**Assets › Renewals** (settings for administrators). New installations remind **30 and 15 days** before a warranty, AMC contract or rental ends. If you saved other days before, change them here.

| Who | Gets |
|---|---|
| Your team (addresses in the settings) | Every reminder |
| The customer's own team (Companies › Edit › "Your team for this customer") | That customer's reminders |
| The customer's contact (optional) | Their own renewals |
| The person the asset is assigned to (optional) | Their asset's warranty, when "Assigned to" holds an email address or the name of a DeskAsk user |
| The vendor (optional; per vendor it can be switched off) | Warranties and AMC contracts bought from them, asking for a renewal quotation; replies go to your team's addresses |

Each reminder goes out once per end date and per day setting. A mail lists each item with:
- customer and site
- serial number
- purchase date
- PO and invoice
- vendor (not shown in the vendor's own letter)

The wording is in **Mail › Templates**, under `asset_renewal` and `asset_renewal_vendor`. Customer administrators can now see their own company's renewals.

## Changes in v3.4.0 (assets)

- **Changing an asset's IP address.** Open the asset, choose Edit and change "IP address". It is the address DeskAsk uses to reach the device (monitoring, remote tools, auto-install). A typed address is kept: an agent only fills the field when it is empty. An address that is not a valid IPv4 or IPv6 address is refused; clearing the field is allowed.
- **Customer administrators and AMC contracts (optional).** Under Assets › Renewals an administrator can tick "Let a customer administrator add, change and renew the AMC contracts of their own company". It is off by default. When on, a customer administrator sees the AMC page with only their own company's contracts and can add, edit, renew and delete them, using only their own company's assets and vendors. Rentals stay with administrators. Turning the option off removes their access straight away.
- **Fields no longer run outside dialogs.** In every module, form fields now stay inside the dialog and wrap on narrow windows (the cause was a minimum width on forms that was wider than the dialog's inside space).

## Changes in v3.5.0 (assets)

- **Duplicate protection.** Adding or editing an asset checks the same customer's other active assets (retired and lost ones do not count). A serial number, MAC address or host name that another asset already has is refused (host names are compared without the domain and without regard to case; MAC addresses in any notation; placeholder serials such as "N/A" are ignored). The same IP address or the same name is a warning: "Possible duplicate: Open existing / Add anyway". CSV import skips rows that match an existing asset and reports which one.
- **Assets › Duplicates** lists assets of one customer that look like the same device, and **Merge** keeps one: the asset with the agent is kept (two assets that both have an agent cannot be merged), blanks are filled from the other, MAC addresses are combined, AMC and rental lists, service checks and history move over, and the other asset is retired with the note "Merged into …".
- **Add asset** now takes IP address, MAC address, host name and "Monitor this asset". Changing the IP address or host name of an asset that has an agent asks the agent to report again; an agent-reported address replaces a stored one that is no longer among the device's addresses. After adding, open the asset and use "Install agent through the controller".
- **Edit asset** can put the asset under an AMC and under a rental (`PUT /api/assets/{id}/coverage`), and the Overview shows each contract with its vendor. Rentals now have a vendor and a contract number like AMCs. Vendors come from Vendors (the customer's own and shared ones).
- **Monitoring per asset.** Each asset has a "Monitor this asset" switch (`assets.assets.monitored`, on for existing assets); unmonitored assets are left out of monitoring. Select assets in All assets and use "Monitor selected" or "Stop monitoring selected".

## Changes in v4.1.0 (assets)
- **"Add anyway" is for administrators.** A possible duplicate (same IP address or name as another asset of the customer) can now only be saved by an administrator, or by people and groups an administrator chooses under Assets › Duplicates › *Who may add a possible duplicate anyway* (groups inside groups count). Everyone else sees the matching asset and a note to ask an administrator; the server answers 403 `duplicate_override_forbidden`. A sure duplicate (same serial, MAC address or host name) stays refused for everyone. `GET /api/assets/duplicates/override` tells the web whether to offer the button; `GET/PUT /api/assets/settings/duplicate-override` (administrators) holds the list.
- **MAC addresses are compared by their digits.** `AA-BB-CC-DD-EE-FF`, `aa:bb:..` and `aabb.ccdd.eeff` are the same address. Before this, an address reported by an agent never matched the same address typed by hand. All-zero and all-ones addresses (virtual adapters) never match.
- **Discovery recognises a device by MAC address and host name, not only by IP.** The controller's found list, automatic install, *Add* from the found list, and the server's probe and scan all use the same rule: same MAC first, then the same host name (domain ignored), then the same IP, within the controller's customer. The found list names the asset and the reason ("same MAC address"). A device without an agent that answers at a new address with the same MAC takes the new IP address, with a history line and a note in the controller's activity; an agent-managed device keeps reporting its own address, and a host-name match alone never moves an address.

## Changes in v4.3.0 (assets)
- **Sidebar:** Assets pages are grouped in a sidebar: Inventory, Software, Agents, Contracts, Organisation, Admin.
- **Software licences and subscriptions** (Assets › Licences & subscriptions): what a customer bought (Microsoft 365, Adobe, AutoCAD, Windows Server…), with publisher, edition, kind (subscription, perpetual, volume, OEM, trial, open source), licensing (per user, device, core, site, concurrent), seats, vendor, purchase/start/end dates, billing, cost and currency, PO, invoice, subscription ID, admin portal, notes. A name pattern counts the computers that have it installed (from the software inventory); seats are given to people or computers; over-use shows. The licence key is stored sealed; only people who may change the licence see it, and every look is in the audit log. **Renew** makes the next period linked to this one, seats carried over. Ending licences join the warranty/AMC renewal reminders (30 and 15 days, to your team, the customer and the vendor). CSV export (no keys).
- **Commands on an asset:** categories on the left with counts (13 categories such as Network diagnostics, Disk and storage, Services, Updates, Domain and policy, Printing), commands as cards.
- **Deleting assets:** whether deleting waits for an approver is a setting (Assets › Settings), **off** by default. Off: people who may edit an asset can delete it, giving a reason and typing the asset tag; deletion is permanent and the reason is audited (set the status to Retired to keep the record). On: as before, people without the delete right send a request.

## Accessories and personal devices (5.5)

Monitors, keyboards, mice, docking stations, headsets, webcams, mobile phones, tablets, SIM cards and "other device" are kinds of asset that ship with DeskAsk. Each one is an asset of its own with **its own serial number** (unique across the customer's assets), because a supplier asks for the desktop's serial number when a keyboard is replaced.

- **Not reached remotely.** A kind marked so (Admin → Kinds of asset) has no agent, shell, remote control or monitoring, and does not count in the "no agent" ring. Phones keep IMEI, phone number, OS, storage and carrier as extra details.
- **Attached to a main device.** Choose the desktop or laptop when adding the accessory, or use "Attach to a main device" on its page. Whatever the accessory lacks (purchase date, warranty, vendor, invoice, site, room, holder, status) is copied from the main device. Two levels only: an attached item cannot have items. Searching the desktop's serial number lists the desktop and everything attached to it.
- **Add several.** All assets → Add several: paste serial numbers, one per line, or give a quantity (up to 200). Duplicates are skipped and listed.
- **People.** What each person holds, take several things back at once (attached items come along), give things from stock, and print the handover sheet for signing.
- **Stock.** Counts per kind: in stock, in use, in repair, retired, lost.

## 6.3: Reports, Services and Uninstall

**Assets › Reports** lists every asset report: inventory, hardware and firmware, disks and partitions, network adapters, installed software (search a program to see where it is), antivirus, patch compliance, purchase/warranty/AMC, licences and subscriptions, agent health, and the Backup status report. Each can be filtered by customer and status, searched, and exported to CSV (UTF-8, opens in Excel). The page shows 2,000 rows; the CSV holds up to 50,000. Customer scope applies to every row.

**Services tab** (computers with the agent): every service with its state, startup type and, on Windows, the account it runs as. Administrators and engineers can Start, Stop, Restart and change the startup type (Automatic, Automatic (delayed start), Manual, Disabled; on Linux enable or disable). The agent runs the action as a job (visible in Remote activity) and the row shows the state the computer reported afterwards. The same buttons are on the Monitoring device page.

**Log On** (Windows, administrators, with a reason): Local System, Network Service, Local Service, or an account with its password, typed or filled from the Vault (decrypted in the browser; the use is recorded on the Vault item). The password travels through the one-time sealed hand-over: the agent fetches it once, Windows sets it with ChangeServiceConfig, and the account gets "Log on as a service". It is never in a job, a script, the history or the audit log. Needs agent 6.3 or later.

**Applications tab › Uninstall** (administrators and engineers, with a reason): the program's silent uninstall entry on Windows (QuietUninstallString, or msiexec /x for MSI packages), the package manager on Linux (apt, dnf, yum, zypper). A program without a silent uninstaller is not removed and the message says so. The reason goes to the asset history and the audit log; the inventory refreshes afterwards.

## 6.5: Printers

Set a printer's *Read as* to **Printer or other device (SNMP)** and give it a saved SNMP login. The site controller then reads it every 15 minutes ("Read now" at most every 2 minutes) and the asset gets a **Printer** tab:
- **Status**: idle, printing, warming up, plus faults (jam, door open, no paper, offline, service needed), the display message and the printer's own alerts.
- **Supplies**: toner, ink, drums, fusers and waste containers with colour and percentage; trays with paper size and level.
- **Pages**: lifetime counter, pages this month, last month, 7 and 30 days, and a daily chart. A replaced printer (counter goes down) gives no figure for that period.
- **Clear the printer's queue**: IPP Purge-Jobs on port 631, cancelling job by job if the printer does not support it. Works on standalone printers; a printer that asks for a login says so.
- **Restart the printer**: SNMP write (v2c) needing a *write* community saved on the asset. A read-only community is refused with a clear message.

Print servers: clear their queues with the library scripts *Clear print queue and restart spooler* and *List printers*. Printers behind a print server are still read directly over SNMP.

Not in 6.5: colour vs black-and-white page split, print-server queue monitoring, SNMPv3 restart. Tested against simulated printers only. Update the site controllers to read printers.

## Assets opens on All assets; basic details in the list (6.8.1)

- Opening Assets (the menu, "Open Assets", the breadcrumb, or coming back from an asset) now lands on **All assets**. The dashboard is Assets › **Dashboard** (`#/assets/dashboard`).
- The list shows, for every asset: **Status** (usage: in use, in stock, repair, retired, lost), **OS**, **IP address** and the **agent** state (online, offline, update due, no agent), next to tag, name (with make and model), type, person, site and warranty. OS and IP come from the agent or controller and are blank for devices that report none.

## People from the customer's domain controller

Devices setup › Customers › **Domain controller** (with a single company: Devices setup › Domain controller) connects a customer's Active Directory so its people can be used in Assets. It works with an Assets or Monitoring licence; AD Manager is not needed.

- It reuses the AD Manager connection (table `adsuite.domains`) and the controller installed in the customer's network, so nothing is opened to the internet. The account only needs to read. If the customer later buys AD Manager, the same domain appears there.
- DeskAsk reads the people (name, email, department, title, enabled) every 6 hours (job `adsuite.people`) and on **Sync now**, and keeps them in `assets.directory_people`. A list is replaced only when every domain of the customer answered, so a controller that is down never empties it. Someone who leaves the directory leaves the list at the next read.
- **Inventory › People** lists them next to the people who hold assets (a directory person with nothing handed out shows 0 items).
- Wherever an asset is given to someone (Allocate, Edit › Assigned to, People › Give), the name box suggests the customer's directory people as you type. A name can still be typed by hand (customers without a domain controller, visitors, contractors).
- API: `GET/PUT/DELETE /api/directory/companies/{id}`, `POST …/test`, `POST …/sync` (administrators); `GET /api/assets/directory-people?companyId=&search=&limit=` (anyone who may read assets, limited to their customers).

## Customer filter and Stock links (6.15.0)
- **All assets** has a Customer list next to Status: all customers, one customer, or "No customer". In single-company mode it is hidden.
- **Stock**: every count opens All assets filtered to that kind, that status, and the chosen customer. Total opens every status. Assets without a kind open with `typeId=none`.

## A company's users (6.16.0)
Administration › Devices setup › Customers › choose a customer. With one company, it is Devices setup › **Our organisation**. Tabs:
- **Users**: the company's customer administrators and employees.
  - **Add user** (name, email, role, password, and the Service Desk switches *May open tickets* and *May reply and close*).
  - **Add an existing user** ties customer users who belong to no company yet.
  - **Assets** shows how many things each person holds, found under their name or their email. Choose the number to see them, take several back, give more from stock, or print the hand-over sheet.
  - Edit, Set password, Disable, Remove from company.
- **Domain controller**: the read-only connection used to suggest names when assets are handed out.
- **Sign-in** (6.18.0) and **AD permissions** (6.20.0): see sign-in.md and admanager.md.
- **Details**: name, contact, address, remote access, link to its assets and locations.

What moved:
- Assets › People is gone; each person's things are on their company's Users tab.
- Service Desk › Settings › People (Customer users) is gone; its switches are on the Users tab.
- Administration › Users lists only your own team (administrators, engineers, viewers). The roles offered for new people from Microsoft, Google or AD sign-in are staff roles.

A customer user's company is kept in one place (Access, `access.user_companies`). The Service Desk reads it there, so a person tied to the company by single-company mode can open tickets straight away. Links made before 6.16 in `servicedesk.people` still count.
- **Pick from the domain controller** (6.17.0): turns people of the company's directory into users who sign in with their Windows password. See [sign-in.md](sign-in.md#a-companys-employees-with-their-windows-password-6170).

## Software repository (6.19.0)
Assets › Software › **Repository**: the approved programs engineers install on computers.

**Adding a program** (administrators):
- **Installer:** upload a `.msi` or `.exe` (Windows), or a `.deb`, `.rpm` or `.sh` (Linux), up to 2 GB.
  - The upload goes in 8 MB pieces, so a proxy in front does not refuse it, and resumes where it stopped.
  - The server keeps the file with its SHA-256. Give the vendor's SHA-256 to have it checked on arrival.
  - Files are kept in `Assets:PackageDir`, by default `<install folder>\data\packages\<customer>`. Back that folder up with `data`.
- **winget id** (for example `Google.Chrome`) or **Chocolatey id** for Windows.
- **Package name** for the Linux package manager (apt, dnf, yum or zypper).
- **Silent switches**: an `.msi` always gets `/qn /norestart`. For an `.exe`, give its own switches (for example `/S`, `/quiet`, `/verysilent`), or it may wait for a person and be stopped after 55 minutes.

**Installing** (administrators and engineers with the Assets *change* right):
- From the repository: **Install on…**, then choose Windows or Linux computers with a live agent.
- From an asset: Applications › **Install from the repository**.
- Each computer gets its own job (Logs tab, live output, exit code). The computer downloads the installer over a link that works for one day, checks the SHA-256, installs it silently and deletes the download. Exit codes 0, 1641 and 3010 count as success (3010: restart to finish).
- winget runs as SYSTEM through App Installer's own copy. A computer without App Installer, or without Chocolatey, says so in the log.
- Computers that are offline, have no agent, are blocked for remote tools, or run another system are skipped with the reason.
- The number under **Installs** opens where and when the program was installed, by whom, and each result.

Removing a program from the repository deletes its file. Computers that have it keep it; uninstall it from their Applications tab. Audit: `assets.package_added`, `assets.package_changed`, `assets.package_removed`, `assets.package_install`. Tables: `assets.packages`, `assets.package_installs` (migration 32).

## Relationships between assets (6.22.0)

The asset page has a **Relationships** tab. Link an asset to another with one of: *runs on* (a virtual machine on its host), *depends on* (an application on a database), *is part of*, or *is connected to* (a server to its switch). The tab lists links in both directions and, under it, **If this asset fails**: every asset that would suffer, nearest first, with its open tickets. The same list shows on a ticket whose asset is down.

How the walk goes: *runs on*, *depends on* and *is part of* mean the first asset suffers when the second fails; *connected to* counts both ways. It follows up to four steps, never loops, and shows at most 200 assets. Customer administrators see only assets of their own company. Adding and removing links needs the right to modify assets and is written to the audit log (`assets.relation_added`, `assets.relation_removed`). API: `GET/POST /api/assets/{id}/relations`, `DELETE /api/assets/relations/{id}`, `GET /api/assets/{id}/impact`.

## Required and prohibited software (6.22.0)

Edit a program in the repository and choose a **Company rule**: *Required* (every computer of its system should have it) or *Prohibited*. "Name in the inventory" is part of the name as the agent lists it (empty means the program's own name; case is ignored). The repository page then lists **Computers that break a software rule**, from the last program list each agent sent. Computers that never sent a list, and retired or lost ones, are left out. API: `GET /api/assets/packages/violations?company=<id>`.

## Rows per page (6.22.0)

The asset list, the ticket list and the Monitoring device list have a "Rows per page" choice (10, 25, 50, 100 or 200), remembered in the browser for each list.

## 6.23.0
- Ask DeskAsk: see [assistant.md](assistant.md).
