# Branding

Administration › Branding lets a customer's administrators make DeskAsk look like their own: logo, name, header and footer text, accent and menu colours, and a light or dark theme.

## Who can do what

- **Read** (`GET /api/branding`, `GET /api/branding/logo`): anyone, signed in or not. The sign-in page needs the look before anyone has signed in. Nothing secret is stored here.
- **Change** (`PUT /api/branding`, `PUT|DELETE /api/branding/logo`, `POST /api/branding/reset`): the `admin` role of that customer only. Engineers and customer users get "403 Forbidden". Every change is written to the audit log (`branding.updated`, `branding.logo_changed`, `branding.logo_removed`, `branding.reset`).
- Branding is **per customer**: each customer's administrators change only their own installation's look.

## What can be set

| Setting | Limit | Where it shows |
|---|---|---|
| Name | 60 characters | Menu, browser tab title, sign-in page |
| Logo | PNG, JPEG, WebP, GIF or SVG, up to 256 KB | Menu, sign-in page |
| Header text | 120 characters | Top bar of every page |
| Footer text | 300 characters | Bottom of every page, sign-in page |
| Accent colour | `#rrggbb` | Buttons, links, focus rings, the marker beside the open menu entry |
| Second colour | `#rrggbb` | The short end of the stripe across the top of the menu and the sign-in box |
| Menu colour | `#rrggbb` | Left-hand menu; empty means white (text colour is chosen automatically for contrast) |
| Theme | auto, light, dark | Auto follows each person's computer; light or dark forces it for everyone |

Text colour on the accent is chosen automatically (black or white, whichever reads better). The page warns when an accent colour is hard to read on both light and dark backgrounds.

**Reset to standard** removes every setting and the logo.

## How a logo is kept safe

A logo is the one place where an administrator can put a file in front of everyone's browser, so it is checked on upload and served locked down:

- The file's first bytes must match its declared type (a `.png` that is really a web page is refused).
- An SVG is refused if it contains scripts, event handlers, embedded pages or objects, `javascript:` addresses, animation elements, style imports or any reference to an outside address. Export a plain SVG, or use a PNG.
- It is served with `X-Content-Type-Options: nosniff` and a `sandbox` content security policy, so even opening its address directly cannot run anything. It is only ever drawn through an `<img>` tag.
- All text is HTML-encoded on the sign-in page and set as plain text in the web app.

## Where it is stored

Two per-customer settings: `branding` (the values and a logo version number used to refresh caches) and `branding.logo` (type and data). No new tables, so an update needs no migration.

## Browser-tab icon (6.3)

Administration › Branding › Browser-tab icon: upload a square PNG, SVG or ICO (up to 128 KB; 32 x 32 or 64 x 64 pixels is enough). It is used in the browser tab, bookmarks and on the sign-in pages. Without one, the logo is used; without a logo, the standard DeskAsk icon. "Go back to the standard look" removes it too.

## Reports and exports (PDF) design

Administration › Branding has a **Reports and exports (PDF)** card. It sets the look of every PDF this installation makes: the service report, the uptime report and the asset export.

- **Table header colour** and **Table header letters**: the band over each table and its text. The standard is the logo's teal `#207178` with white letters.
- **Borders**, **Letters**, **Headings and lines**: black borders and text by default; the teal rule under headings.
- **Line at the foot of every page**: for example your company name and website (up to 120 characters).

Colours are written `#rrggbb`. The small preview on the card shows the table as it will print. **Save design** keeps it; **Use the standard design** puts the logo colours back (press Save to keep that). Each change is written to the audit log (`branding.pdf_changed`, `branding.pdf_reset`).

## Your own web address

Administration › Branding › *Your own web address* lets a customer open DeskAsk at their own name (desk.example.com) next to the built-in one (acme.deskask.in), which keeps working.

1. The administrator adds the address (up to 5; addresses under `Platform:ReservedDomains`, default `deskask.in`, are refused).
2. DeskAsk shows two DNS records: a **TXT** record `_deskask-verify.<address>` with a one-time value (proves the address is theirs) and a **CNAME** from the address to the built-in address (or `Platform:CustomDomainTarget` when set).
3. **Check** reads DNS. Once the TXT value is found the address is verified: it finds the customer, and the web sign-in is registered for it (no restart).
4. **Use in links and emails** makes it the main address: mail links and the address agents are given use it.

Certificates: the proxy asks `GET /api/platform/domain-check?domain=<name>` before it requests a certificate. The answer is 200 only for a verified address, so nobody can make the proxy request certificates for other names (Caddy `on_demand_tls { ask ... }`; HAProxy/ACME setups use the same check). Database: `platform.tenant_domains` (catalog migration 2, created on first use).
