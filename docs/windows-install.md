# Installing DeskAsk Platform on Windows Server

Tested in CI on every push (windows-latest): install, run as a service under a limited account, upgrade by running the script again, uninstall.

## What you need
- Windows Server 2019 or newer, PowerShell as Administrator.
- PostgreSQL 15 or newer (same server or another) with a login that may create databases. Make an empty database for the catalog, for example `deskask_catalog`.
- From the GitHub Release page (https://github.com/faiselfaizee/IT-MANAGED-SERVICES/releases), three files in one folder:
  - `deskask-host-win-x64-<version>.dkb`
  - `deskask-updater-windows-amd64.exe`
  - `release-public.pem`
- No .NET installation is needed: the Windows bundle carries its own runtime.

## The setup page (recommended)
Put `deskask-setup.ps1` and `install-windows.ps1` in the folder with the three release files, then, in PowerShell as Administrator:
```powershell
.\deskask-setup.ps1
```
A page opens in your browser (it listens on this server only, behind a one-time address). It has two tabs, each with a live log:

1. **Install the platform (once).** Choose the folder (any drive, not only `C:`), check the three file paths (found automatically when they sit next to the script), enter the PostgreSQL details, press **Test connection**, then **Install / update**. Optional on the same tab: *Create this login and database for me* (uses the PostgreSQL administrator, needs `psql.exe`), and *Publish through IIS* (one site that passes every address to the platform; choose the certificate from the list). With IIS in front, a new customer needs only a DNS record.
2. **Create a customer (each new customer).** Name, short id, address, administrator's email. The page shows the generated administrator password once, in a box with a Copy button. The same tab lists customers and can give an administrator a new password.

Close it with **Close setup**. Installing is a one-time job: later versions are uploaded in the platform or applied by running the page again with the new bundle. The install log is also kept in `<install folder>\logs\setup-install-*.log`.

The IIS part needs the *URL Rewrite* and *Application Request Routing* modules; the page says so if they are missing. It is not covered by the automatic tests (they run on a machine without ARR), so check it once on a real server.

## Install from the command line
This is what the page runs. Use it for scripted installs.
```powershell
cd C:\Install        # the folder with the three files
.\install-windows.ps1 `
  -Root D:\DeskAsk `
  -Bundle .\deskask-host-win-x64-0.1.0.dkb `
  -Updater .\deskask-updater-windows-amd64.exe `
  -PublicKey .\release-public.pem `
  -CatalogConnection "Host=localhost;Database=deskask_catalog;Username=deskask;Password=YOUR-PASSWORD" `
  -Urls "http://localhost:5000"
```
(`-Root` defaults to `C:\DeskAsk`. The scripts are in the repository under `deploy/windows/`.)

It does the following (paths relative to the install folder):
1. Creates the install folder with `inbox`, `state`, `logs`, `config`, `data`.
2. Sets permissions. Administrators and SYSTEM may change everything. The platform runs as **LocalService**, may only read, and writes only to `inbox` and `data`. It cannot change `live`, `previous` or the release public key.
3. Writes `config\deskask.json` with a new encryption secret and the paths. **Back this file up.** On later runs it is kept as it is.
4. Creates the Windows service **DeskAskPlatform** (automatic start, restarts after a crash).
5. Checks the bundle's signature, installs it into `live`, starts the service and waits for `/health`.
6. Creates the startup task **DeskAskUpdater** (runs as SYSTEM): it watches `inbox`, so updates uploaded from the platform are applied, and it rolls back a version that does not start.

## First customer
Use tab 2 of the setup page, or:
```powershell
C:\DeskAsk\deskask-tenant.cmd create --id acme --host acme.example.com --admin-email you@example.com --name "Acme Ltd"
```
The command prints the first administrator's password once. If it was lost: `deskask-tenant.cmd reset-admin --id acme`. More in `docs/provisioning.md`.

## Making it reachable
`-Urls http://localhost:5000` is only for this machine. Two ways to publish it:
- **IIS as the front door (recommended):** install URL Rewrite and Application Request Routing, create a site with your certificate, and add a reverse-proxy rule to `http://localhost:5000`. The proxy must pass the original host name on (ARR: "Preserve client host header"), because each customer is found by the address they use. When the platform listens on this machine only, it believes the proxy's `X-Forwarded-Proto` (sign-in needs to know the outside address was https); `Platform:TrustProxyHeaders=true` does the same for other setups. The setup page does all of this for you.
- **Direct:** `-Urls "https://+:443"` needs a certificate configured for Kestrel; the script opens the firewall port when the address is not local.

## Updating
Either upload the new `.dkb` in the platform (two-step sign-in required), or run the script again with the new bundle. Then run `C:\DeskAsk\deskask-tenant.cmd migrate` to bring all customer databases up to date.

## Remove
`.\install-windows.ps1 -Uninstall` removes the service and task and keeps data and configuration. Add `-RemoveFiles` to delete `C:\DeskAsk` too (customer databases stay in PostgreSQL).

## Licence
Put the vendor public key and licence into `config\deskask.json` as `Licensing:VendorPublicKeyPem` and `Licensing:File` (see ADR 0008), then restart the service: `Restart-Service DeskAskPlatform`.

## If something fails
`C:\DeskAsk\logs\updater.log`, the Windows Application event log (source "DeskAskPlatform"), and `Get-Service DeskAskPlatform`.
