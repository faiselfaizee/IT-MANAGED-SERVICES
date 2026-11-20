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
