# On-prem installation, start to finish

This is the whole path for a customer running DeskAsk on their own server. It covers what to prepare, the install, the licence, the settings to make on the first day, and then backups, updates and moving servers. Each step links to the page with the details.

On-prem and SaaS run the same program. The differences are: one customer per server (usually), the customer's own address and certificate, a licence key instead of a licence from the catalog, and the customer's administrator applying updates.

## 1. Before you start

### Server
- Windows Server 2019 or newer. A suggested starting size is 4 CPU cores, 8 GB RAM and 100 GB of disk; monitoring history and file backups make the disk grow.
- PowerShell as Administrator.
- No .NET installation is needed: the Windows bundle carries its own runtime.

### Database
- PostgreSQL 15 or newer, on the same server or another.
- A login that may create databases (each customer gets its own database `deskask_<id>`), and an empty database for the catalog, for example `deskask_catalog`.
- The setup page can create the login and the database if you give it the PostgreSQL administrator password and `psql.exe` is installed.

### Files (from the GitHub release page, all in one folder)
- `deskask-host-win-x64-<version>.dkb` (the platform)
- `deskask-updater-windows-amd64.exe`
- `release-public.pem`
- `deskask-setup.ps1` and `install-windows.ps1` (from `deploy/windows/` in the repository)

### Name and certificate
- A DNS name for the server, for example `helpdesk.customer.com`, pointing at the server or at the proxy in front of it.
- A certificate for that name (public CA or the customer's internal CA). IIS or the proxy ends TLS; DeskAsk listens on `http://localhost:5000` behind it.
- A custom domain from Branding is **not** needed on-prem: the server is already reached by the customer's own name.

### Network (open only what is listed)

| Direction | From → To | Port | Why |
|---|---|---|---|
| In | Users and agents → server | TCP 443 | web app, agents, site controllers |
| In | → server | TCP 80 | only if certificates are renewed over HTTP |
| Out | Server → platform.deskask.in | TCP 443 | licence activation and the daily licence check |
| Out | Server → login.microsoftonline.com, graph.microsoft.com | TCP 443 | Microsoft sign-in and import, mail via Microsoft 365 |
| Out | Server → accounts.google.com, oauth2.googleapis.com, admin.googleapis.com | TCP 443 | Google sign-in and import |
| Out | Server → mail server | TCP 587 or 465 | mail via SMTP |
| Local | Devices → site controller | UDP 514, UDP 162 | syslog and SNMP traps |
| Local | Site controller → devices | ICMP, SNMP UDP 161, WinRM 5985/5986, SSH 22, LDAP 389 / LDAPS 636 | scans, discovery, agent push, domain controller |
| In | → RustDesk server, if used | TCP 21115–21119, UDP 21116 | remote access |

Without outbound access to platform.deskask.in, use the licence file instead (step 4).

## 2. Install the platform

On the server, in the folder with the files, in PowerShell as Administrator:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\deskask-setup.ps1
```

A page opens in the browser. It listens on this server only, behind a one-time address.

1. Tab **Install the platform**:
   1. Choose the install folder (default `C:\DeskAsk`; any drive works).
   2. Check the three file paths.
   3. Enter the PostgreSQL details and press **Test connection**.
   4. Optionally tick *Create this login and database for me* and *Publish through IIS* (choose the certificate).
   5. Press **Install / update** and wait for the live log to finish.
2. Tab **Create a customer**:
   1. Enter the name (the customer's organisation), short id (for example `acme`), address (`helpdesk.customer.com`) and the administrator's email.
   2. Copy the administrator password. It is shown **once**.
3. Press **Close setup**.

The same steps from the command line, for scripted installs, are in [windows-install.md](windows-install.md#install-from-the-command-line).

**Right after installing, back up `C:\DeskAsk\config\deskask.json` and `C:\DeskAsk\data\keys\`.** They hold the encryption secret and the data-protection keys. Without them, sealed secrets (mail and LDAP passwords, two-step sign-in secrets, signing keys) cannot be read again, even with a database backup.

### Check
```powershell
Get-Service DeskAskPlatform            # Running
Invoke-WebRequest http://localhost:5000/health -UseBasicParsing   # ok
Get-ScheduledTask DeskAskUpdater       # Ready or Running
```
Open `https://helpdesk.customer.com` and sign in with the administrator email and password.

## 3. Publish it (IIS or a proxy)

- **IIS (the setup page does this when *Publish through IIS* is ticked):** one site with the certificate and a reverse-proxy rule to `http://localhost:5000` that keeps the original host name. It needs the *URL Rewrite* and *Application Request Routing* modules.
- **Another proxy in front (HAProxy, nginx, a firewall):** it must keep the `Host` header and send `X-Forwarded-Proto: https` and `X-Forwarded-Ssl: on`. If it does not, sign-in stops with "This server only accepts HTTPS requests" (ID2083).

Details: [windows-install.md](windows-install.md#making-it-reachable).

## 4. Licence

Without a licence every module is switched off.

**With internet (usual):**
1. Administration › Licence.
2. Type the licence key from the order (`DKA-XXXXX-XXXXX-XXXXX-XXXXX-XXXXX`) and activate.
3. The licence is bound to this installation. Renewals, upgrades and cancellations arrive by themselves through the daily check, or at once with **Check now**.

**Without internet:**
1. Administration › Licence shows the customer id and the installation id. Send both to DamazeTech.
2. The vendor issues a licence file (Vendor console › Subscriptions › Licence file).
3. Paste it on the Licence page and press **Install licence**. Renewals come the same way, by hand.

**For the vendor (DamazeTech):** Vendor console › Subscriptions › new subscription, kind **On-prem**, the customer id, the modules and sizes. Then **Create a licence key** (shown once) and send it. A key allows one installation. For an organisation running its own IT, choose an end-customer size: its "What it allows" contains `single_company=1`.

**Single-company mode** switches on by itself with such a licence. The organisation's own name becomes the one company, the customer screens are hidden, and new users are tied to that company. See [licensing.md](licensing.md#single-company-mode-what-changes-6140).

## 5. First-day configuration

Do these in order. Each one is under Administration unless stated.

1. **Mail** (Administration › Mail). Choose SMTP (server, port, user, password, From) or Microsoft 365. For Microsoft 365, create an app registration in Entra with the *Mail.Send* application permission, grant admin consent, and enter the tenant id, application id and secret. Set **Public address of DeskAsk** to `https://helpdesk.customer.com` so links in mail are right. Press **Send test**. Failed mail waits in the outbox and can be retried there.
2. **Two-step sign-in.** Each administrator sets it up (account menu › Security). Then turn on "require two-step sign-in" for administrators and engineers (Users).
3. **Users and sign-in** (Users). Add engineers, or connect Microsoft, Google or Active Directory sign-in and import people. See [sign-in.md](sign-in.md). The redirect address to register is `https://<your address>/account/sso/microsoft/callback` (or `/google/callback`).
4. **Branding** (Administration › Branding). Logo, colours, report and PDF design. See [branding.md](branding.md).
5. **Devices setup** (Administration › Devices setup):
   1. **Credentials**: Windows, SSH and SNMP logins used for discovery and agent push.
   2. **Agents & collectors**: create an enrolment token, then copy the install command.
      - Install a **site controller** on one server in the network: `deskask-agent collector-install --server https://helpdesk.customer.com --token dkco_...`.
      - Install **agents** on computers with the install script (`install.ps1` / `install.sh`), Group Policy, Intune, or a push from the controller.
   3. **Domain controller** (single-company mode only): connect the organisation's domain controller through the site controller so Assets › People shows the directory users. It syncs every 6 hours, or at once with **Sync now**.
   4. **Discover**: scan address ranges for devices.
   See [assets.md](assets.md#general-agent-and-site-controller-collector).
6. **Monitoring**: review the starting alert rules, set recipients and an optional Teams channel. Point device syslog and traps at the site controller. See [monitoring.md](monitoring.md).
7. **Service Desk**: categories, SLA, knowledge base. See [servicedesk.md](servicedesk.md).
8. **AD Manager, Vault, Backup**, if licensed: [admanager.md](admanager.md), [vault.md](vault.md), [backup.md](backup.md).
9. **RustDesk**, if remote access is used: see [assets.md](assets.md#rustdesk-set-up-with-a-live-log).

## 6. Backing up the DeskAsk server

Back up all three, together:

| What | Where | How |
|---|---|---|
| Databases | PostgreSQL: the catalog (`deskask_catalog`) and every customer database (`deskask_<id>`) | `pg_dump -Fc` each one nightly, or a full `pg_dumpall` |
| Configuration | `C:\DeskAsk\config\deskask.json` | copy after any change |
| Data-protection keys and files | `C:\DeskAsk\data\` (includes `data\keys`) | file backup nightly |

To restore on a new server: install the same version (step 2) with the same `config\deskask.json`, put `data\` back, restore the databases with `pg_restore`, start the service, then run `C:\DeskAsk\deskask-tenant.cmd migrate`.

## 7. Updates

The customer's administrator updates the program:
- **From the platform:** Administration › Updates, upload the new `.dkb`. Two-step sign-in is required. The updater applies it within seconds and rolls back by itself if the new version does not start.
- **Or** run `deskask-setup.ps1` again with the new bundle.

Afterwards run `C:\DeskAsk\deskask-tenant.cmd migrate` (databases are also brought up to date on the first request). Agents and site controllers update themselves. See [updates.md](updates.md).

## 8. Moving to a new server

1. Old server: Administration › Licence › **Release this installation**.
2. Back up as in step 6 and restore on the new server.
3. New server: activate the same licence key.
4. Point DNS at the new server. Agents and controllers follow the name.

If the old server is already gone, the vendor releases the installation in the Vendor console.

## 9. Uninstall

```powershell
.\install-windows.ps1 -Uninstall                 # removes the service and task, keeps data and configuration
.\install-windows.ps1 -Uninstall -RemoveFiles    # also deletes the install folder
```
Customer databases stay in PostgreSQL until you drop them.

## Checklist to hand over to the customer

- [ ] `https://<address>` opens with a valid certificate and sign-in works
- [ ] Licence shows the right modules and end date
- [ ] Test mail arrives
- [ ] Two-step sign-in is required for administrators
- [ ] Site controller online; agents checking in
- [ ] Backups of databases, `config\deskask.json` and `data\` running and tested once
- [ ] The administrator password is changed and stored in a vault
