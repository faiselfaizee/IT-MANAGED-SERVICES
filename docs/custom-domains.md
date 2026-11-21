# Customers' own web addresses (operations)

A customer adds `desk.example.com` under Administration › Branding (see branding.md). Custom domains are part of every end-customer plan. This page is for the person running the proxy.

## What the customer does
1. Adds the address in Branding and creates the two DNS records shown: TXT `_deskask-verify.<address>` (value shown) and CNAME `<address>` → the customer's built-in address (`Platform:CustomDomainTarget` overrides the target, e.g. one shared name such as `edge.deskask.in`). Prefer a sub-domain (`desk.example.com`); an apex domain cannot hold a CNAME at most DNS providers.
2. Presses **Check**. The address works as soon as the TXT record is found.

## What the proxy must do
The proxy has to (a) accept the new name and send it to the platform unchanged (keep the `Host` header) and (b) hold a certificate for it.

- **Certificate on demand (Caddy, Traefik, nginx+lua):** ask the platform before issuing. `GET https://<any platform host>/api/platform/domain-check?domain=<name>` answers 200 only for a verified address and 404 for anything else, so nobody can make the proxy request certificates for other names. Caddy: `on_demand_tls { ask https://platform.deskask.in/api/platform/domain-check }`.
- **Proxy without on-demand issuing (HAProxy/OPNsense + ACME plugin):** run a small script on a schedule that reads the list and adds each new name to the certificate and the frontend. Set `Platform:ProxyKey` (16+ characters) on the platform, then `GET /api/platform/proxy/domains` with header `X-Proxy-Key: <key>` returns `{"hosts":["desk.example.com", ...]}`. Without the key set the endpoint answers 404.
- The platform also registers the web sign-in redirect for the address by itself (no restart).

## Good to know
- The built-in address keeps working; agents already installed keep using it. New install commands use the address the administrator is signed in at.
- "Use in links and emails" makes the address the main one: mail links and the customer's host in the catalog use it. Mail with `Mail PublicUrl` set keeps that URL.
- Microsoft/Google sign-in: add `https://<address>/account/sso/microsoft/callback` and `/account/sso/google/callback` to the allowed redirect addresses of that sign-in.
- Removing an address stops it opening DeskAsk at once; the certificate is left to expire.
- Table `platform.tenant_domains` holds the addresses (catalog migration 2).
