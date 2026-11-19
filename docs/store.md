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

# Release 6.1: the store

The public store is at `https://<vendor platform>/#/store` (no sign-in). On the development server it runs on damazetech.deskask.in with `Commerce:VendorTenant=damazetech`.

## Before it opens

1. **Vendor console › Business details**: legal name, address, GSTIN (needed), CIN, PAN, SAC (998315), LUT reference for exports, support email and phone, grievance officer, invoice prefix (default DA), links to the legal pages, the domain for customer addresses (default deskask.in), trial days (14) and how long unconverted trials are kept (45). Change the address here when the registrations change; invoices already issued keep their details.
2. **Vendor console › Payments**: the Razorpay key id and key secret, and a webhook secret. In Razorpay add a webhook to `https://<vendor platform>/api/store/webhooks/razorpay` with the events `payment.captured` and `order.paid`. A `rzp_test_` key means test mode: no real money is taken.
3. **Signing key** imported (6.0), and for SaaS customers the wildcard DNS and HAProxy rule (`*.deskask.in` to the platform).

The store says "opens soon" until the business details and the Razorpay keys are set; trials need only the business details.

## Buying

- Prices come from the price list and are worked out on the server. India: rupees plus GST (CGST + SGST 9% each in Tamil Nadu, else IGST 18%; the state comes from the buyer's GSTIN, else the state chosen). Outside India: US dollars, no GST, "export under LUT" on the invoice. On-prem is 20% below SaaS, yearly only; monthly SaaS is the yearly price / 12 + 20%. 4 or more modules: 25% off. Monitoring needs Asset management.
- Checkout makes the order and a Razorpay order, then opens Razorpay Checkout. The payment is accepted when the signed answer from Checkout (HMAC of order and payment id with the key secret) or the signed webhook arrives; whichever comes first completes the order, the other does nothing.
- **SaaS**: the customer is created (database, catalog row, every module), licensed for the period (+7 grace days), and the buyer becomes the first administrator with a set-password link (works once, 72 hours). A buyer who is on a trial with the same email and address keeps their trial customer: it becomes the paid plan.
- **On-prem**: a licence key, shown on the order page for 7 days and emailed.
- **Invoice**: numbered `DA/2026-27/00001` (one series per financial year), PDF emailed and kept; Vendor console › Invoices.
- An order that is paid but could not be completed shows as "Needs attention" under Orders with the reason; **Try again** repeats the work.

## Free trials

The trial form creates the customer at once with the smallest size of each chosen module for the trial days; the set-password email also proves the address. At most 3 trials a day per email, email domain or address; throwaway email domains are refused. In the console: **Extend 7 / 14 days**, or **Delete customer and data** (type the customer id). The job `commerce.trials` emails the trial's contact 7 days before deletion and deletes unconverted trials the set number of days after they end.

## Mail

The vendor customer's mail settings send the store's messages (templates `store_welcome`, `store_trial`, `store_licence_key`, `store_invoice` with the PDF, `store_trial_ending`).

# Release 6.2: the website, the built-in key and the store switch

## Turning the store on
The vendor licence public key is built into every release (`src/Platform/DeskAsk.Platform.Licensing/VendorPublicKey.pem`), so no key goes in the configuration. `Licensing:VendorPublicKeyPem` (or `Licensing:VendorPublicKeyFile`) still replaces it when set; a value that cannot be read is ignored, the built-in key is used, and Administration › Licence and Vendor console › Signing key say what is wrong. The server never stops because of it.

Turn the store on for the vendor's own customer with one command on the server (Windows, from the install folder):

```
deskask-tenant.cmd store-enable --id damazetech
Restart-Service DeskAskPlatform
```

It checks the customer exists, sets `Commerce:Enabled` and `Commerce:VendorTenant` in the configuration file (DESKASK_CONFIG, or `--config <file>`), removes a `Licensing:VendorPublicKeyPem` that cannot be read, keeps every other setting, and saves the old file next to it as `.bak-<date>`. `store-disable` turns it off.

## The website
`https://<vendor platform>/#/store` is now a website: home, Products (one page per module with its sizes and prices), Pricing (the checkout), Free trial, FAQ, About, Contact and the policy pages (Terms, Privacy, Cancellation and refunds, Delivery). The module texts are in `web/src/store/content.ts`.

The About and policy pages start as the draft from the legal review and are edited in **Vendor console › Website pages** (Markdown). "Go back to the draft" restores the built-in text. A link set under Business details (Terms URL and so on) replaces the built-in page in the footer.
