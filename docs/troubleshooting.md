# Troubleshooting

Where to look first: `C:\DeskAsk\logs\` (platform and updater logs), the Windows Application event log (source "DeskAskPlatform"), `Get-Service DeskAskPlatform`, and Administration › Audit.

## A new customer's address does not open (SaaS)
**Symptom:** the customer exists and the trial or purchase finished, but `https://acme.deskask.in` shows the IIS start page, a 404 or no site.
**Cause:** IIS has no binding for names under the domain, so it does not send the request to DeskAsk.
**Fix**, once, on the IIS server (elevated PowerShell):
```powershell
Import-Module WebAdministration
New-WebBinding -Name 'DeskAsk' -Protocol http -Port 80 -HostHeader '*.deskask.in'
```
Check with `Get-WebBinding -Name 'DeskAsk'`. If another proxy (HAProxy, OPNsense) is in front, it needs a wildcard rule for `*.deskask.in` that sends to IIS and passes the original host name. New installs made with the setup page get the binding by themselves.

## Administration › Licence shows "Valid, Development, until 9999-01-01"
**Cause:** `Licensing:AllowUnlicensed` is `true` in the server configuration (`config\deskask.json` or an environment variable). It is a development switch that gives every module.
**Fix:** remove it (or set it to `false`) and restart: `Restart-Service DeskAskPlatform`. From 6.5.1 it is ignored on a shared SaaS server (one with a customer catalog), so a customer there always shows the licence the catalog holds.

## Pasting or activating a licence key answers "The server answered 500"
From 6.5.1 the page shows the real reason instead of a bare 500, and the full error is in the platform log (search for "Activating a licence key failed"). Known causes:
- **Development licence is active** (see above). Fixed in 6.5.1: the date shown in the audit entry no longer overflows.
- **The licence server cannot be reached:** the server must reach `https://platform.deskask.in` (or `Licensing:ActivationUrl`) over HTTPS. Test with `Invoke-WebRequest https://platform.deskask.in/api/link/v1/ping -UseBasicParsing` (a 401 means it is reachable).
- **The customer has no licence in the catalog** (SaaS): issue one in Vendor console › Subscriptions; the customer's page then shows it within a minute.
- **The vendor key is wrong:** see the next entry.

## "Licensing:VendorPublicKeyPem in the configuration is not a valid public key"
**Meaning:** the configuration file has a `Licensing:VendorPublicKeyPem` line whose value is not a P-256 public key (often cut or pasted without line breaks). The server ignores it and uses the key built into the release, so licences still verify; this is a warning, not a failure.
**Fix:** delete that line from `config\deskask.json` (or run `deskask-tenant store-enable --id <vendor customer>`, which removes an unreadable key for you) and restart the service. Only keep the setting if you were given a different vendor key, pasted whole with `\n` between its lines.

## The Updates page is missing for a customer (SaaS)
By design: on the shared server only the vendor's own customer updates the program. See [windows-install.md](windows-install.md#who-updates-the-program).

## A product is not shown on the website
Vendor console › Prices › **Products on sale**: tick it on. The website asks the platform for the list, so there is nothing to change on the website; if it still shows the old list, wait a minute or press "Send a test event" in Website link.

## Reports page, dashboards
Nothing to configure. If a customer's tile is missing from a dashboard, the customer has no data for that module yet.

## Website link: "The website could not be reached: No connection could be made because the target machine actively refused it (dev.deskask.in:443)"
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
