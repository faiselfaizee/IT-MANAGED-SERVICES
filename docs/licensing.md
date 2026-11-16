# Licences: modules and limits

A licence is signed with the vendor's private key and names the customer (or `*`), the plan, the dates and the **modules** it includes. A module that is not in the licence is switched off for that customer: its menus are hidden, its pages answer 402 and its background jobs do nothing.

Issue a licence with the licence tool (`tools/DeskAsk.LicenseTool`) or the browser generator (`tools/licence-generator.html`):

```
deskask-license issue --key vendor-private.pem --tenant acme --plan business --days 365 \
  --modules servicedesk:agents=10,assets:max_assets=300,monitoring:max_devices=150,vault:max_users=25
```

Module names: `servicedesk`, `assets`, `monitoring`, `adsuite` (AD Manager), `vault`, `backup`. **Monitoring needs `assets`** in the same licence (it watches the devices Assets holds).

## Limits

A limit is written `module:limit=number`; several limits of one module are joined with `+` (`assets:max_assets=300+customer_write=1`). A limit that is not written means no limit. The tool refuses limit names it does not know.

| Module | Limit | Counts | When reached |
|---|---|---|---|
| assets | `max_assets` | assets in service (not retired or lost) | adding by hand, CSV import, discovery and automatic agent installs stop; bringing a retired asset back is refused |
| assets | `customer_write=1` | (a switch) | customer administrators may be given more than read access |
| monitoring | `max_devices` | assets with "Monitor this asset" on, in service | the oldest devices up to the limit are watched, the rest are not (the Monitoring dashboard says how many); switching monitoring on for more is refused |
| servicedesk | `agents` | active administrators and engineers | adding, importing or promoting another administrator or engineer is refused, and so is creation at first Microsoft or Google sign-in with such a role; viewers and customer users are not counted |
| adsuite | `max_domains` | AD domains | adding another domain is refused |
| vault | `max_users` | people who have set up the Vault | a new person cannot set up their Vault (someone resetting theirs can) |
| backup | `max_devices` | devices with a file backup plan | a plan for another device is refused (more plans for a counted device are fine); watching existing backup software is not limited |

A refusal is HTTP 402 `{"error":"limit_reached","module":…,"limit":…,"allowed":…,"detail":…}`, shown in the page. Nothing already there is removed when a smaller licence is installed.

Administrators see each module's **used / allowed** under Administration › Licence.
