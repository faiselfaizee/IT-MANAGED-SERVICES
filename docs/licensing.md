# Licences: modules and limits

A licence is signed with the vendor's private key and names the customer (or `*`), the plan, the dates and the **modules** it includes. A module that is not in the licence is switched off for that customer: its menus are hidden, its pages answer 402 and its background jobs do nothing.

**From release 6.0, licences are issued from the vendor console** on the vendor platform (Administration › Vendor console, see [store.md](store.md)): SaaS customers are licensed directly, on-prem customers get a licence key. The licence tool stays for emergencies (the platform is down) and for making or changing the signing key. The browser generator (`tools/licence-generator.html`) is retired: it needs the private key loaded into a web page.

The licence tool:

```
deskask-license issue --key vendor-private.pem --tenant acme --plan business --days 365 \
  --modules servicedesk:agents=10,assets:max_assets=300,monitoring:max_devices=150,vault:max_users=25
```

Module names: `servicedesk`, `assets`, `monitoring`, `adsuite` (AD Manager), `vault`, `backup`. Monitoring can be licensed on its own (6.6.0): customers, logins, agents and discovery are shared "foundation" under Administration › Devices setup and work with either `assets` or `monitoring`. With Monitoring only, the device cap is `monitoring:max_devices`.

## Limits

A limit is written `module:limit=number`; several limits of one module are joined with `+` (`assets:max_assets=300+customer_write=1`). A limit that is not written means no limit. The tool refuses limit names it does not know.

| Module | Limit | Counts | When reached |
|---|---|---|---|
| assets | `max_assets` | assets in service (not retired or lost) | adding by hand, CSV import, discovery and automatic agent installs stop; bringing a retired asset back is refused |
| assets | `customer_write=1` | (a switch) | customer administrators may be given more than read access |
| assets, monitoring or servicedesk | `single_company=1` | (a switch, 6.9.0) | **single-company mode** for an organisation that runs its own IT: its own name is the one company (made automatically) and a second company is refused (409 `single_company`). `GET /api/platform/mode` tells the web. Set it on a product size in the Vendor console (Price list › Add a size › "What it allows": `max_assets=100, single_company=1`), so end-customer plans carry it and MSP plans do not |
| monitoring | `max_devices` | assets with "Monitor this asset" on, in service | the oldest devices up to the limit are watched, the rest are not (the Monitoring dashboard says how many); switching monitoring on for more is refused |
| servicedesk | `agents` | active administrators and engineers | adding, importing or promoting another administrator or engineer is refused, and so is creation at first Microsoft or Google sign-in with such a role; viewers and customer users are not counted |
| adsuite | `max_domains` | AD domains | adding another domain is refused |
| vault | `max_users` | people who have set up the Vault | a new person cannot set up their Vault (someone resetting theirs can) |
| backup | `max_devices` | devices with a file backup plan | a plan for another device is refused (more plans for a counted device are fine); watching existing backup software is not limited |

A refusal is HTTP 402 `{"error":"limit_reached","module":…,"limit":…,"allowed":…,"detail":…}`, shown in the page. Nothing already there is removed when a smaller licence is installed.

Administrators see each module's **used / allowed** under Administration › Licence.

## When a licence ends

A licence works until its end date, then for its **grace days**. After that the modules it included turn **read-only**: menus and data stay, every change is refused (HTTP 402 `{"error":"license_expired","readOnly":true}`), and background work (monitoring, backups, reminders, automation) stops. A banner says so on every page. Nothing is deleted; installing a renewed licence switches everything back on.

## Online activation (on-prem)

Under Administration › Licence an administrator types the **licence key** (`DKA-XXXXX-XXXXX-XXXXX-XXXXX-XXXXX`) from the order. The installation sends the key with its **installation id** (a random id made at first use, setting `license.installation`) and customer id to the licence server (`Licensing:ActivationUrl`, default `https://platform.deskask.in`) and receives a licence **bound to this installation**: a copy on another installation is refused. Once a day (job `licensing.refresh`), and on **Check now**, it asks for a newer licence, so renewals, upgrades and cancellations arrive by themselves. Only the activation id and its secret are sent; no usage or customer data.

- **Moving to a new server:** **Release this installation** on the old one, then activate the new one with the same key. The vendor can also release it from the console.
- **No internet:** the Licence page shows the customer id and installation id; the vendor issues a licence file for them, which is pasted under "Install a new licence".
- The installation needs outbound HTTPS to the licence server for activation and the daily check. Without it the current licence keeps working until it ends.
