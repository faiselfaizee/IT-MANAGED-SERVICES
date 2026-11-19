# Vendor console and licensing service (release 6.0)

The vendor console runs on the vendor's own platform (platform.deskask.in). It keeps customers, subscriptions, every licence issued, licence keys and the price list, and it signs licences with the vendor key held on the server. Release 6.1 adds the public store and payments on top of it (plan: "DeskAsk Store and automatic licensing").

## Turning it on (vendor platform only)

| Setting | Value |
|---|---|
| `Commerce:Enabled` | `true` |
| `Commerce:VendorTenant` | the customer id of the vendor's own tenant on this platform (default `platform`) |
| `Commerce:Connection` | optional; default the catalog (`Platform:CatalogConnection`). Tables live in schema `commerce`. |
| `Commerce:SigningKeyPem` | optional; the vendor private key. Otherwise import it once in the console (it is sealed with data protection and never shown again). |
| `Licensing:VendorPublicKeyPem` | must be the public half of that key; the console refuses a key that does not match. |

Only administrators of the vendor tenant see **Administration › Vendor console**. On every other installation and tenant the console and store addresses answer 404.

Keep the data-protection keys of the platform backed up: a stored signing key cannot be read without them. Keep your own offline copy of `vendor-private.pem` as well.

## Console

- **Subscriptions**: customer, SaaS or on-prem, customer id, modules and sizes (from the price list or a custom number), start and end dates, grace days (default 7 SaaS, 14 on-prem), plan name, billing period, notes.
  - **SaaS**: saving licenses the customer at once (the licence goes into the catalog, `platform.licenses`). Changing the subscription or **End now** issues the new licence at once; ending turns their modules read-only.
  - **On-prem**: **Create a licence key** shows the key once; the customer types it on their Licence page. A key allows 1 installation by default. **Withdraw** a key to stop renewals and new activations; **Release** an installation to free its place. **Licence file** issues a licence for an offline site from the customer id and installation id they read off their Licence page.
- **Not linked yet**: SaaS customers licensed before the store. **Link** makes a subscription from the licence they already have (nothing changes for them). An on-prem licence issued with the licence tool can be pasted to link it too.
- **Price list**: per module and size, SaaS yearly and monthly (USD), on-prem yearly (USD) and India yearly (INR, before GST). Seeded with the 6.0 price list.
- **Signing key**: whether licences can be issued, where the key is kept and its key id.

Every action is written to the vendor tenant's audit log (`commerce.*`).

## Licensing service (called by installations)

| Address | Body | Answer |
|---|---|---|
| `POST /api/store/activate` | `key, installation, tenant, host, version` | `token, activation, secret`; 404 unknown key, 410 withdrawn, 409 already in use or another customer id, 402 subscription ended |
| `POST /api/store/licence` | `activation, secret` | `token` (issued again when the subscription changed since the last one); 404/410 when the activation is unknown, released or withdrawn |
| `POST /api/store/release` | `activation, secret` | frees the activation |

No sign-in; limited per address like the other public addresses (`Security:AuthPerMinute`). Keys and activation secrets are stored only as SHA-256 hashes. A licence issued by activation names the installation; other installations refuse it.

## Tables (schema `commerce`)

`plans`, `accounts`, `subscriptions`, `licences` (every licence issued, with who and why), `licence_keys`, `activations`, `orders` (used from 6.1), `settings` (the sealed signing key).
