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

## Install
```powershell
cd C:\Install        # the folder with the three files
.\install-windows.ps1 `
  -Bundle .\deskask-host-win-x64-0.1.0.dkb `
  -Updater .\deskask-updater-windows-amd64.exe `
  -PublicKey .\release-public.pem `
  -CatalogConnection "Host=localhost;Database=deskask_catalog;Username=deskask;Password=YOUR-PASSWORD" `
  -Urls "http://localhost:5000"
```
(`install-windows.ps1` is in the repository under `deploy/windows/`.)

It does the following:
1. Creates `C:\DeskAsk` with `inbox`, `state`, `logs`, `config`, `data`.
2. Sets permissions. Administrators and SYSTEM may change everything. The platform runs as **LocalService**, may only read, and writes only to `inbox` and `data`. It cannot change `live`, `previous` or the release public key.
3. Writes `config\deskask.json` with a new encryption secret and the paths. **Back this file up.** On later runs it is kept as it is.
4. Creates the Windows service **DeskAskPlatform** (automatic start, restarts after a crash).
5. Checks the bundle's signature, installs it into `C:\DeskAsk\live`, starts the service and waits for `/health`.
6. Creates the startup task **DeskAskUpdater** (runs as SYSTEM): it watches `inbox`, so updates uploaded from the platform are applied, and it rolls back a version that does not start.

## First customer
```powershell
C:\DeskAsk\deskask-tenant.cmd create --id acme --host acme.example.com --admin-email you@example.com --name "Acme Ltd"
```
The command prints the first administrator's password once. More in `docs/provisioning.md`.

## Making it reachable
`-Urls http://localhost:5000` is only for this machine. Two ways to publish it:
- **IIS as the front door (recommended):** install URL Rewrite and Application Request Routing, create a site with your certificate, and add a reverse-proxy rule to `http://localhost:5000`. The proxy must pass the original host name on (ARR: "Preserve client host header"), because each customer is found by the address they use.
- **Direct:** `-Urls "https://+:443"` needs a certificate configured for Kestrel; the script opens the firewall port when the address is not local.

## Updating
Either upload the new `.dkb` in the platform (two-step sign-in required), or run the script again with the new bundle. Then run `C:\DeskAsk\deskask-tenant.cmd migrate` to bring all customer databases up to date.

## Remove
`.\install-windows.ps1 -Uninstall` removes the service and task and keeps data and configuration. Add `-RemoveFiles` to delete `C:\DeskAsk` too (customer databases stay in PostgreSQL).

## Licence
Put the vendor public key and licence into `config\deskask.json` as `Licensing:VendorPublicKeyPem` and `Licensing:File` (see ADR 0008), then restart the service: `Restart-Service DeskAskPlatform`.

## If something fails
`C:\DeskAsk\logs\updater.log`, the Windows Application event log (source "DeskAskPlatform"), and `Get-Service DeskAskPlatform`.
