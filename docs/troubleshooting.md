# Troubleshooting

Each entry gives the **Symptom** (what you see), the **Cause**, the **Fix**, and how to **Check** it. Headings use the words shown on screen, so search this page for the message you see.

## First checks (always)

Run these on the DeskAsk server, in PowerShell as Administrator:
```powershell
Get-Service DeskAskPlatform                                         # must be Running
Invoke-WebRequest http://localhost:5000/health -UseBasicParsing     # must answer ok
Get-ScheduledTask DeskAskUpdater                                    # Ready or Running
Get-Content C:\DeskAsk\logs\updater.log -Tail 50
Get-WinEvent -LogName Application -MaxEvents 30 | Where-Object ProviderName -eq 'DeskAskPlatform' | Format-List TimeCreated, Message
```
In the web app: Administration › Audit for what people and jobs did, Administration › Licence for modules and dates, and Administration › Mail › outbox for mail that did not go.

Where things are:

| What | Where |
|---|---|
| Configuration | `C:\DeskAsk\config\deskask.json` ([configuration.md](configuration.md)) |
| Program (current, previous) | `C:\DeskAsk\live`, `C:\DeskAsk\previous` |
| Logs | `C:\DeskAsk\logs\` (updater, setup); platform messages in the Windows Application log, source DeskAskPlatform |
| Keys and files | `C:\DeskAsk\data\` |
| Customer command line | `C:\DeskAsk\deskask-tenant.cmd` ([provisioning.md](provisioning.md)) |
| Agent on a computer | Windows: scheduled task `DeskAskAgent` (site controller: `DeskAskCollector`), program in `C:\Program Files\DeskAskAgent`. Linux: systemd units `deskask-agent` / `deskask-collector`. `deskask-agent inventory` shows what it would report. |

The sections below are grouped: installation and start-up, reaching the site and signing in, licence, mail, users and directory sign-in, agents and site controllers, domain controller and AD Manager, custom domains, updates, single-company mode, and vendor platform and website.

## Installation and start-up

### The setup page does not open, or the script stops at once
**Cause:** PowerShell blocks scripts, or the window is not elevated.
**Fix:** open PowerShell **as Administrator**, then `Set-ExecutionPolicy -Scope Process Bypass` and `.\deskask-setup.ps1`. If the files came from a download, `Unblock-File .\*.ps1`.

### "Test connection" to PostgreSQL fails
**Check, in order:**
1. PostgreSQL is running: `Get-Service postgresql*`.
2. It listens on the address used: `listen_addresses` in `postgresql.conf` (use `localhost` on the same server).
3. `pg_hba.conf` allows the login from this server (`host all deskask 127.0.0.1/32 scram-sha-256`), then restart PostgreSQL.
4. The login may create databases: `ALTER ROLE deskask CREATEDB;`.
5. Port 5432 is open between the servers: `Test-NetConnection <db server> -Port 5432`.

### The service DeskAskPlatform does not start, or stops after a few seconds
**Look at:** the Application event log, source DeskAskPlatform, for the first error.
**Usual causes:**
- **The catalog cannot be reached** (password changed, PostgreSQL down). Fix `Platform:CatalogConnection` in `config\deskask.json` and restart.
- **Port 5000 is already used** by another program: `Get-NetTCPConnection -LocalPort 5000`. Stop that program, or install with another `-Urls`.
- **`config\deskask.json` is not valid JSON** after a hand edit. Check with `Get-Content C:\DeskAsk\config\deskask.json -Raw | ConvertFrom-Json`. Restore the backup copy if needed.
- **The data folder cannot be written**: the service runs as LocalService and may write only `inbox` and `data`. Run the installer again to reset the permissions.

### "A step that was changed after it was applied is refused" (database migration)
**Cause:** a program version with a different database step than the one already applied, usually a test build installed over a release.
**Fix:** install the official release bundle of the same or a newer version. Never edit `meta.migrations` by hand. Then `deskask-tenant.cmd migrate`.

### Everything worked, then after a restart people are signed out and saved mail or LDAP passwords "cannot be read"
**Cause:** the data-protection keys were not on persistent storage, or `Identity:EncryptionSecret` changed. Sealed secrets can only be opened with the same keys and secret.
**Fix:** put back `C:\DeskAsk\data\keys` and the original `config\deskask.json` from backup. If there is no backup, enter the mail, LDAP, Teams and device passwords again, and people set up two-step sign-in again (an administrator resets it under Users).
**Prevent:** keep `Platform:DataProtectionPath` on a persistent folder, and back it up with the configuration ([install-onprem.md](install-onprem.md#6-backing-up-the-deskask-server)).

## Reaching the site and signing in

### The address does not open from other computers
**Check, in order:**
1. On the server, `Invoke-WebRequest http://localhost:5000/health -UseBasicParsing` answers ok. If not, the platform is down: see the previous section.
2. DNS: `Resolve-DnsName helpdesk.customer.com` gives the server's (or the proxy's) address.
3. IIS site is started, and has a binding for the name on 443 with the certificate: `Get-WebBinding -Name 'DeskAsk'`.
4. Firewall allows TCP 443 in: `Test-NetConnection helpdesk.customer.com -Port 443` from a client.
5. The IIS reverse-proxy rule points at `http://localhost:5000`, and ARR has *Enable proxy* on.

### "This server only accepts HTTPS requests" (ID2083) when signing in
**Cause:** a proxy ends https and talks plain http to IIS or DeskAsk without saying the original request was https.
**Fix:** on the proxy, send `X-Forwarded-Proto: https` and `X-Forwarded-Ssl: on` (HAProxy: `http-request set-header X-Forwarded-Proto https` and `http-request set-header X-Forwarded-Ssl on` on the https frontend). If the proxy is on another machine and talks to DeskAsk directly, also set `Platform:TrustProxyHeaders=true`.

### The page opens but shows another customer, or "unknown site"
**Cause:** the proxy replaces the host name. DeskAsk finds the customer by the address used.
**Fix:** keep the original `Host` header (ARR: "Preserve client host header"; HAProxy keeps it by default; nginx: `proxy_set_header Host $host;`). Check the customer's address with `deskask-tenant.cmd list`.

### "Too many requests" (429) on the sign-in page
**Cause:** more than 30 sign-in or public requests a minute from one address. This often happens when many people come through one NAT address and the proxy does not pass the client's address.
**Fix:** wait for the time in `Retry-After`. If many users share one address, raise `Security:AuthPerMinute`, or make the proxy send `X-Forwarded-For` and set `Platform:TrustProxyHeaders=true` when it is on another machine.

### The administrator password is lost
```powershell
C:\DeskAsk\deskask-tenant.cmd reset-admin --id acme
```
It prints a new password once. Add `--email someone@acme.com` to reset a different administrator.

### A person is "locked" or two-step sign-in is lost
Lost authenticator: an administrator opens Users and presses **Reset two-step** for the person, who sets it up again at the next sign-in. Locked after repeated wrong passwords: the lock ends by itself after a short wait; an administrator can also set a new password for the person.

## Licence

### Every page says the licence has ended, and changes are refused ("license_expired", read-only)
**Cause:** the end date and the grace days have passed. Data stays; changes and background work stop until a valid licence is installed.
**Fix:** renew. With internet, Administration › Licence › **Check now** fetches the renewed licence. Offline, install the new licence file from the vendor.

### A module is missing from the menu, or a page answers 402
**Cause:** the module is not in the licence, or a limit is reached (`limit_reached`, the message names the limit).
**Fix:** Administration › Licence shows modules and "Used / allowed". Buy a bigger size or the module, then **Check now**. Nothing is removed when a limit is reached.

### Activating the key: "already in use" (409)
**Cause:** the key is bound to another installation (an old server, or a copy).
**Fix:** on the old server, Licence › **Release this installation**. If the old server is gone, ask the vendor to release it in Vendor console › Subscriptions. Then activate again.

### Activating the key: "unknown key" (404), "withdrawn" (410), or "subscription ended" (402)
Typing error (check the key), a key the vendor withdrew, or a subscription past its end. The vendor checks it in Vendor console › Subscriptions.

### Administration › Licence shows "Valid, Development, until 9999-01-01"
**Cause:** `Licensing:AllowUnlicensed` is `true` in the server configuration (`config\deskask.json` or an environment variable). It is a development switch that gives every module.
**Fix:** remove it (or set it to `false`) and restart: `Restart-Service DeskAskPlatform`. From 6.5.1 it is ignored on a shared SaaS server (one with a customer catalog), so a customer there always shows the licence the catalog holds.

### Pasting or activating a licence key answers "The server answered 500"
From 6.5.1 the page shows the real reason instead of a bare 500, and the full error is in the platform log (search for "Activating a licence key failed"). Known causes:
- **Development licence is active** (see above). Fixed in 6.5.1: the date shown in the audit entry no longer overflows.
- **The licence server cannot be reached:** the server must reach `https://platform.deskask.in` (or `Licensing:ActivationUrl`) over HTTPS. Test with `Invoke-WebRequest https://platform.deskask.in/api/link/v1/ping -UseBasicParsing` (a 401 means it is reachable).
- **The customer has no licence in the catalog** (SaaS): issue one in Vendor console › Subscriptions; the customer's page then shows it within a minute.
- **The vendor key is wrong:** see the next entry.

### "Licensing:VendorPublicKeyPem in the configuration is not a valid public key"
**Meaning:** the configuration file has a `Licensing:VendorPublicKeyPem` line whose value is not a P-256 public key (often cut or pasted without line breaks). The server ignores it and uses the key built into the release, so licences still verify; this is a warning, not a failure.
**Fix:** delete that line from `config\deskask.json` (or run `deskask-tenant store-enable --id <vendor customer>`, which removes an unreadable key for you) and restart the service. Only keep the setting if you were given a different vendor key, pasted whole with `\n` between its lines.


## Mail

### "Send test" fails
**SMTP:**
- Port and security must match: 587 with STARTTLS, or 465 with SSL.
- Test the route from the DeskAsk server: `Test-NetConnection smtp.example.com -Port 587`.
- Microsoft 365 SMTP needs *Authenticated SMTP* enabled for the mailbox. With security defaults on, use the Microsoft 365 provider instead.

**Microsoft 365:**
- The app registration must have the **Mail.Send application** permission, and admin consent must be granted.
- The From address must be a real mailbox in that tenant.
- The secret must not have expired (Entra › App registrations › Certificates & secrets).

### Mail is sent but links in it point to the wrong address
Set **Public address of DeskAsk** in Administration › Mail. With a custom domain, use **Use in links and emails** under Branding.

### Mail waits in the outbox
Each mail is tried again with growing pauses. After the cause is fixed, press **Try again** next to the failed mail in the outbox.

## Users and directory sign-in

### Microsoft sign-in: "AADSTS50011: The redirect URI … does not match"
Add exactly `https://<address>/account/sso/microsoft/callback` to the app registration's Web redirect URIs. Add one per address, including each custom domain. Google: add `/account/sso/google/callback` to the OAuth client.

### Microsoft import finds nobody, or "Insufficient privileges"
The app needs **Microsoft Graph › Application › User.Read.All** with admin consent ([sign-in.md](sign-in.md)).

### Active Directory sign-in or import: "cannot connect" or "certificate"
- The server must reach the domain controller on 636 (LDAPS): `Test-NetConnection dc01.acme.local -Port 636`.
- If the controller's certificate is from an internal CA, install that CA in the DeskAsk server's *Trusted Root* store, or tick "Accept the controller's certificate".
- Plain LDAP (389) with signed binding works only when DeskAsk runs on Windows.

## Agents and site controllers

### An agent shows "offline"
**Meaning:** no check-in for 15 minutes (`Assets:AgentOfflineSeconds`).
**Check on the computer:**
1. The agent runs. Windows: `Get-ScheduledTask DeskAskAgent` (start with `Start-ScheduledTask DeskAskAgent`; a five-minute watchdog also restarts it). Linux: `systemctl status deskask-agent`.
2. It reaches the server: `Test-NetConnection helpdesk.customer.com -Port 443`.
3. A proxy or TLS-inspecting firewall is not breaking the connection. Allow the DeskAsk address without inspection.
4. The server name in its configuration is still right. After a server move, DNS must point at the new server.

### Installing the agent: "the token is not valid" or "expired"
Create a new enrolment token in Administration › Devices setup › Agents & collectors and copy the install command again. A revoked token cannot be used for new installs.

### Pushing the agent to Windows fails
See [assets.md](assets.md): "Windows push: WinRM is running but the install fails" and "The WinRM client cannot process the request". The controller must run on Windows to push to Windows, and WinRM (5985/5986) must be open on the target.

### Devices do not send syslog or traps
Point them at the **site controller's** address (UDP 514 and UDP 162), not the DeskAsk server. Messages from addresses that are not devices of that site are ignored; the controller log names them.

### RustDesk set-up stays on "working…"
See [assets.md](assets.md#rustdesk-set-up-stays-on-working-and-nothing-starts-on-the-computer).

## Domain controller and AD Manager

### Domain controller "Test" fails, or People shows a sync error
DeskAsk never talks to the domain controller directly: requests go through the site controller. So:
1. The chosen site controller is online (Agents & collectors).
2. From the controller's server: `Test-NetConnection dc01.acme.local -Port 636` (or 389).
3. The base DN is right (for example `DC=acme,DC=local`) and the service account's password works.
4. LDAPS certificate problems: Test names the reason ([admanager.md](admanager.md#ldaps-v411)).
The People list keeps the last good copy until every domain answers. **Sync now** retries at once.

### Assets › People is empty
Single-company mode only: Administration › Devices setup › **Domain controller** must be set up and synced once. With several customers, set up each customer's domain controller under Devices setup › Customers › Domain controller.

## Custom domains

### "We could not find the TXT record _deskask-verify.<address>"
DNS changes can take time. Check from any computer: `Resolve-DnsName -Type TXT _deskask-verify.desk.example.com`. The value must match the one shown exactly, with no quotes added by the DNS panel. Then press **Check** again.

### The custom address is verified but shows a certificate warning
The proxy has no certificate for that name yet. With on-demand certificates, the proxy must ask `/api/platform/domain-check`. With HAProxy/OPNsense, the scheduled script must have run (`/api/platform/proxy/domains` with `X-Proxy-Key`). See [custom-domains.md](custom-domains.md).

## Updates

### An uploaded update did nothing, or the old version came back
**Cause:** the updater refused the bundle (bad signature or contents), or the new version did not answer `/health` and was rolled back.
**Look at:** `C:\DeskAsk\logs\updater.log`.
**Fix:** download the bundle again from the release page (check it against `SHA256SUMS.txt`). If it rolled back, the event log of that start says why. Check that the task DeskAskUpdater is running: `Start-ScheduledTask DeskAskUpdater`.

### After an update, a customer sees an error on the first page
Run `C:\DeskAsk\deskask-tenant.cmd migrate` and read its output. It names the customer and the step that failed.

## Single-company mode

### Customer screens (customer tiles, customer pickers) still show
**Cause:** the licence has no `single_company=1`, or the browser has an old copy of the page.
**Fix:** Administration › Licence must show the end-customer size. If the size is wrong, the vendor corrects the subscription and you press **Check now**. Then reload the page with Ctrl+F5.

### "This plan is for one company" when adding a company
By design: an end-customer plan has exactly one company, your own organisation.

## Vendor platform and website

### A new customer's address does not open (SaaS)
**Symptom:** the customer exists and the trial or purchase finished, but `https://acme.deskask.in` shows the IIS start page, a 404 or no site.
**Cause:** IIS has no binding for names under the domain, so it does not send the request to DeskAsk.
**Fix**, once, on the IIS server (elevated PowerShell):
```powershell
Import-Module WebAdministration
New-WebBinding -Name 'DeskAsk' -Protocol http -Port 80 -HostHeader '*.deskask.in'
```
Check with `Get-WebBinding -Name 'DeskAsk'`. If another proxy (HAProxy, OPNsense) is in front, it needs a wildcard rule for `*.deskask.in` that sends to IIS and passes the original host name. New installs made with the setup page get the binding by themselves.

### The Updates page is missing for a customer (SaaS)
By design: on the shared server only the vendor's own customer updates the program. See [windows-install.md](windows-install.md#who-updates-the-program).

### A product is not shown on the website
Vendor console › Prices › **Products on sale**: tick it on. The website asks the platform for the list, so there is nothing to change on the website; if it still shows the old list, wait a minute or press "Send a test event" in Website link.

### Reports page, dashboards
Nothing to configure. If a customer's tile is missing from a dashboard, the customer has no data for that module yet.

### Website link: "The website could not be reached: No connection could be made because the target machine actively refused it (dev.deskask.in:443)"
**Meaning:** the platform server tried to open `https://dev.deskask.in/store/hooks` and the address answered "refused" on port 443. Events (`product.changed`, `order.fulfilled`) wait and are retried for 3 days, so nothing is lost; they arrive once the address works.
**Usual cause when the website and the platform are both behind the same firewall:** from inside the network, the website's public name resolves to the firewall's public IP, and the firewall does not send inside traffic for its own public address back in ("NAT reflection" or "hairpin" is off), or HAProxy listens only on the outside interface.
**Check, on the platform server:**
```powershell
Resolve-DnsName dev.deskask.in
Test-NetConnection dev.deskask.in -Port 443
Test-NetConnection 10.10.10.150 -Port 443
Test-NetConnection 10.10.10.150 -Port 80
```
If the public name fails but the website's own address works, give the platform server the inside answer: an Unbound host override on OPNsense (Services › Unbound DNS › Overrides) for `dev.deskask.in` (and later `www.deskask.in`) pointing at the HAProxy inside address, or turn on NAT reflection. If HAProxy ends the https and the website listens only on http, the name must point at HAProxy, not at the website server. Finally the website must answer `POST /store/hooks`: Website Admin › Store must hold the same secret, and "Send a test event" must show 200.
