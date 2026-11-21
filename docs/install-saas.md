# Running the vendor platform (SaaS)

This page is for DamazeTech, which runs the shared platform at platform.deskask.in. Many customers live on one server, each at its own address, for example `acme.deskask.in`. The same installation issues every licence (SaaS and on-prem) and runs the store.

The program is the same as on-prem. Install it exactly as in [install-onprem.md](install-onprem.md) steps 1 to 3. This page covers what is extra.

## Layout

| Part | Where (today) | Notes |
|---|---|---|
| Firewall and proxy | OPNsense with HAProxy, ACME certificate `*.deskask.in` (DNS-01) | ends TLS, keeps the `Host` header, sends `X-Forwarded-Proto: https` and `X-Forwarded-Ssl: on` |
| DeskAsk platform | Windows Server, IIS in front of `http://localhost:5000` | production 10.10.10.101 (platform.deskask.in); development 10.10.100.101 (damazetech.deskask.in) |
| PostgreSQL | with the platform | catalog plus one database per customer |
| Website | IIS, 10.10.10.150 (www.deskask.in; dev.deskask.in for testing) | separate repository `deskask-website` |
| RustDesk | rustdesk.deskask.in | default RustDesk ports must be reachable from outside |
| DNS | Globehost | `*.deskask.in` A record to the public IP |

Customer data for SaaS stays in India.

## 1. Wildcard addresses

Every new customer gets `<id>.deskask.in`. For that to open without touching anything:
1. **DNS:** a wildcard A record `*.deskask.in` to the public IP.
2. **Certificate:** one wildcard certificate `*.deskask.in` on the proxy.
3. **HAProxy:** specific names first (`www.deskask.in`, `dev.deskask.in` → website backend; `rustdesk.deskask.in` if proxied), then a catch-all rule for `*.deskask.in` → the platform's IIS.
4. **IIS:** a binding for all names under the domain:
   ```powershell
   Import-Module WebAdministration
   New-WebBinding -Name 'DeskAsk' -Protocol http -Port 80 -HostHeader '*.deskask.in'
   ```

Check: `Get-WebBinding -Name 'DeskAsk'`, then open a test customer's address.

## 2. Turn on the vendor console and the store

Once, on the platform server (PowerShell as Administrator, in the install folder):
```powershell
.\deskask-tenant.cmd store-enable --id damazetech
Restart-Service DeskAskPlatform
```
This sets `Commerce:Enabled` and `Commerce:VendorTenant`. Only the vendor customer's administrators then see Administration › Vendor console.

Then, in Vendor console:
1. **Signing key**: import `vendor-private.pem` once. It is sealed and never shown again. Keep an offline copy, and back up `data\keys` (without them the stored key cannot be read).
2. **Business details**: company name, GSTIN, address, invoice design.
3. **Price list**: modules and sizes, with SaaS yearly and monthly, on-prem yearly and India prices.
   - MSP sizes: limits only, for example `max_assets=300`.
   - End-customer sizes (an organisation running its own IT): add `single_company=1`, for example `max_assets=100, single_company=1`. These appear under "My own company" on the website.
4. **Products on sale**: tick what the website should list.
5. **Payments**: the Razorpay key id, key secret and webhook secret. In Razorpay, add a webhook to `https://<vendor platform>/api/store/webhooks/razorpay` for `payment.captured` and `order.paid`. Test with Razorpay test keys on the development platform first.

Details: [store.md](store.md).

## 3. Link the website

1. Vendor console › **Website link**: website address, press **Make the secret**, copy it (shown once).
2. Website › Admin › Store: platform address and the secret, then **Test the link**.
3. Vendor console › Website link › **Send a test event**: it must answer 200.

Both servers must reach each other over HTTPS by their public names. When both are behind the same OPNsense, give the platform server the inside answer for the website's name (Unbound host override) or turn on NAT reflection. Details: [website-link.md](website-link.md), and the troubleshooting entry "Website link: The website could not be reached".

Website deploy and updates (`Update-DeskAsk.ps1`): `deskask-website` repository, `deploy/README.md`.

## 4. Adding a customer

New customers usually come in by themselves: a trial or purchase on the website creates the customer, its database, its address and its licence. To add one by hand:

```powershell
.\deskask-tenant.cmd create --id acme --host acme.deskask.in --admin-email admin@acme.com --name "Acme Ltd"
```
Copy the administrator password it prints (shown once). Then, in Vendor console › Subscriptions, create a SaaS subscription for `acme` with its modules and sizes. The customer's Licence page shows it within a minute.

Other commands (`list`, `disable`, `enable`, `reset-admin`, `migrate`): [provisioning.md](provisioning.md).

## 5. Customers' own addresses (custom domains)

Included in every end-customer plan. The customer adds `desk.example.com` under Administration › Branding and creates the TXT and CNAME records shown. The proxy must then accept the name and hold a certificate for it:
- On-demand certificates (Caddy, Traefik): ask `GET /api/platform/domain-check?domain=<name>` before issuing.
- HAProxy/OPNsense: set `Platform:ProxyKey` (16+ characters). A scheduled script reads `GET /api/platform/proxy/domains` (header `X-Proxy-Key`), then adds each name to the certificate and the frontend.
- Microsoft and Google sign-in: the customer adds `https://desk.example.com/account/sso/microsoft/callback` (and `/google/callback`) to their app.

Details: [custom-domains.md](custom-domains.md).

## 6. Updates on the shared server

Only the vendor customer (`Commerce:VendorTenant`) sees Administration › Updates. Other customers' administrators have no Updates page. After each update run `.\deskask-tenant.cmd migrate` to bring every customer database up to date ahead of their first visit. Release steps: [updates.md](updates.md).

## 7. Backups

The same as on-prem ([install-onprem.md](install-onprem.md#6-backing-up-the-deskask-server)), plus:
- the `commerce` schema in the catalog (orders, invoices, licences, keys)
- the website's own database and uploads
- an offline copy of `vendor-private.pem` and `release-private.pem`
