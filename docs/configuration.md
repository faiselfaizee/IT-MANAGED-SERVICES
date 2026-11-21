# Configuration reference

## Where settings live

- **The configuration file**: `C:\DeskAsk\config\deskask.json`, made by the installer. The service finds it through the `DESKASK_CONFIG` environment variable (the installer sets it, and `deskask-tenant.cmd` sets it too).
- **Environment variables** override the file. Write `:` as `__`, for example `Platform__CatalogConnection`.
- **Restart after a change**: `Restart-Service DeskAskPlatform`.
- **Everything else** (mail, sign-in, branding, alert rules, credentials) is set in the web app and stored in each customer's database, so it does not belong in this file.

A new install writes only this:
```json
{
  "Identity": { "EncryptionSecret": "<random, 48 bytes base64>" },
  "Platform": { "DataProtectionPath": "C:\\DeskAsk\\data\\keys", "CatalogConnection": "Host=...;Database=deskask_catalog;Username=...;Password=..." },
  "Updates":  { "Dir": "C:\\DeskAsk" }
}
```
Running the installer again keeps the file as it is. It changes only `CatalogConnection`, when one is given. Back the file up: losing `EncryptionSecret` or the keys folder makes sealed secrets unreadable.

## Platform and database

| Setting | Default | Meaning |
|---|---|---|
| `Platform:CatalogConnection` | none (an in-memory "dev" customer) | PostgreSQL connection to the catalog. Required in production. |
| `Platform:ProvisionConnection` | the catalog login | a login that may create databases, used by `tenant create` |
| `Platform:TenantConnectionTemplate` | the catalog connection with the database name replaced | connection for new customers, with `{db}` in place of the database name |
| `Platform:DataProtectionPath` | none (keys in memory) | folder for data-protection keys. **Must be persistent and backed up.** |
| `Platform:Roles` | `all` | process roles: `web`, `worker`, `connect`, comma-separated (ADR 0004) |
| `Platform:TrustProxyHeaders` | `false` | trust `X-Forwarded-*` from a proxy that is not on this machine. A proxy on this machine (IIS) is trusted already. |
| `Platform:BootstrapAdmin:Email` / `Password` / `Name` | none | first administrator for a development customer. Real customers get one from `tenant create`. |
| `Platform:DevTenantConnection` | none | database of the in-memory dev customer (development only) |

## Custom domains and the proxy

| Setting | Default | Meaning |
|---|---|---|
| `Platform:ReservedDomains` | `deskask.in` | names customers may not add as their own address (comma-separated) |
| `Platform:CustomDomainTarget` | the customer's built-in address | CNAME target shown to customers, for example `edge.deskask.in` |
| `Platform:ProxyKey` | none (endpoint answers 404) | 16+ characters. Lets a proxy script read `GET /api/platform/proxy/domains` with header `X-Proxy-Key`. |

## Identity and sign-in

| Setting | Default | Meaning |
|---|---|---|
| `Identity:EncryptionSecret` | none | seals two-step secrets and each customer's signing key. **Set once, never change.** |
| `Identity:SigningKeyPem` | none (a key per customer) | one fixed token-signing key for all customers |
| `Identity:Clients:N:Id`, `Secret`, `Name`, `RedirectUris:N`, `PostLogoutRedirectUris:N` | none | extra sign-in clients. Each customer's own address (and verified custom domains) is registered for the web app automatically. |
| `Identity:AllowInsecureHttp` | `false` | allow sign-in over plain http (development only) |
| `Identity:EntraAuthority`, `Identity:GraphUrl` | Microsoft's public addresses | other Microsoft clouds or tests |
| `Identity:GoogleAccounts`, `Identity:GoogleToken`, `Identity:GoogleDirectory` | Google's public addresses | tests |

## Licensing

| Setting | Default | Meaning |
|---|---|---|
| `Licensing:ActivationUrl` | `https://platform.deskask.in` | licence server for activation and the daily check |
| `Licensing:VendorPublicKeyPem` / `Licensing:VendorPublicKeyFile` | the key built into the release | **leave it out** unless you were given another key; an unreadable value is ignored with a warning |
| `Licensing:File` | none | path of a licence file read alongside the licence installed on the Licence page and the one in the catalog |
| `Licensing:CacheSeconds` | `60` | how long a licence read from the catalog is kept |
| `Licensing:AllowUnlicensed` | `false` | development only: every module, no end date. Ignored whenever a catalog connection is set, which includes every server installed with the installer. **Never set in production.** |

## Vendor platform only (store and console)

| Setting | Default | Meaning |
|---|---|---|
| `Commerce:Enabled` | `false` | turns on the vendor console and the store (set by `tenant store-enable`) |
| `Commerce:VendorTenant` | `platform` | customer id of the vendor's own customer |
| `Commerce:Connection` | the catalog | database for schema `commerce` |
| `Commerce:SigningKeyPem` | none | vendor private key (otherwise imported once in the console) |
| `Commerce:StoreUrl` | `https://www.deskask.in/pricing` | where "Renew" and "Buy" links send customers (must be https) |

## Assets, agents and monitoring

| Setting | Default | Meaning |
|---|---|---|
| `Assets:PublicUrl` | the address of the request | address agents and site controllers use to reach the server |
| `Assets:AgentDir` | `<program folder>\agent-dist` | signed agent files served to agents (every release fills it) |
| `Assets:AgentOfflineSeconds` | `900` | time without a check-in before an agent counts as offline |
| `Assets:SecretKey` | `Identity:EncryptionSecret` | key for sealed device credentials |
| `Assets:MaxJobOutput` | built-in limit | largest job output kept from an agent |
| `Monitoring:MetricsSeconds` | `60` (15–3600) | how often agents send metrics |
| `Vault:BreachUrl` | `https://api.pwnedpasswords.com/range/` | password breach check (only a hash prefix is sent) |

## Background jobs

| Setting | Default | Meaning |
|---|---|---|
| `Jobs:PollSeconds` | `5` | how often the worker looks for due jobs |
| `Jobs:BatchSize` | `50` | jobs per customer per round |
| `Jobs:MaxAttempts` | `5` | tries before a job is marked failed (pauses of 10 s, 20 s, 40 s … up to an hour) |
| `Jobs:LeaseSeconds` | `900` | how long a worker holds a job before another may take it |
| `Jobs:TimeoutMinutes` | `10` | longest run of one job |

## Security and updates

| Setting | Default | Meaning |
|---|---|---|
| `Security:AuthPerMinute` | `30` | sign-in and public requests per address per minute (enrolment 120) |
| `Security:RateLimits` | on | `off` turns rate limits off (tests only) |
| `Updates:Dir` | none | install folder the updater watches; the platform writes uploaded bundles to `<Dir>\inbox` |
| `DESKASK_ALLOW_PRIVATE_WEBHOOKS=1` (environment) | off | lets web hooks and Teams reach addresses inside the network |
| `DESKASK_CONFIG` (environment) | none | path of the configuration file |
