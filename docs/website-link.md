# The website link (release 6.4)

The company website (www.deskask.in, the separate `deskask-website` project) sells DeskAsk through this platform. The website shows the
prices, the checkout and the free-trial form; the platform keeps the price list, takes the Razorpay payment, creates the customer, signs
the licence, issues the GST invoice and mails everything, then tells the website how the order went. Razorpay keys exist only here.

```
 Visitor ─► www.deskask.in ──signed──► platform /api/link/v1/...   (prices, quote, checkout, order status, invoice, trial, renew)
                   ▲                         │
                   └──── signed event ◄──────┘  POST https://www.deskask.in/store/hooks   (order.fulfilled, order.failed)
 Razorpay ──webhook──► platform /api/store/webhooks/razorpay   (unchanged)
```

## Setting it up

1. Platform, **Vendor console › Website link**: website address `https://www.deskask.in`; tick "Send visitors of the platform's own store page to the website" when the website store is live. Press **Make the secret** and copy it (shown once).
2. Website, **Admin › Store**: platform address (for example `https://damazetech.deskask.in` for testing, `https://platform.deskask.in` later) and paste the secret. Press **Test the link**: it calls the platform's ping. Switch the store on.
3. Platform, **Website link › Send a test event**: the website must answer 200.
4. The website must reach the platform's address over HTTPS, and the platform must reach `https://www.deskask.in/store/hooks`. With both on the same public IP behind OPNsense/HAProxy, add `www.deskask.in` to HAProxy with its own backend (the website's IIS server) before the tenant catch-all rule.

## Signing

Every request and event carries `X-DeskAsk-Timestamp` (Unix seconds), `X-DeskAsk-Nonce` (16 to 64 characters, used once) and
`X-DeskAsk-Signature: v1=<hex>` where `<hex>` is HMAC-SHA256 with the shared secret over
`timestamp + "\n" + nonce + "\n" + METHOD + "\n" + path-and-query + "\n" + body`. Requests more than 5 minutes off, with a reused nonce or a
wrong signature get 401. Test vector: secret `dkl_test`, timestamp `1700000000`, nonce `0123456789abcdef`, `POST`, `/store/hooks`, body `{}`
gives `v1=7a5d437a36564aa322460c3fac5aef9404259a4fd712e6ab3fed79522984fb96`.

The website passes the visitor's address in `X-DeskAsk-Client-IP` (trusted only on signed requests), so the trial limits count visitors, not the website.

## Routes for the website (`/api/link/v1`)

`GET info`, `GET plans`, `POST quote`, `GET available?id=`, `POST checkout` (adds `ref`, the website's order number, and `renew`, a renewal link),
`POST checkout/confirm`, `GET orders/{id}?t=`, `GET orders/{id}/invoice?t=`, `POST trial`, `GET renew?token=`, `GET ping`. They behave exactly
like the platform's own `/api/store/...` routes (docs/store.md).

## Events for the website

`POST {website}/store/hooks` with `{ id, type, created, data }`: `order.fulfilled` and `order.failed` (data: orderId, ref, kind, status, tenantId,
address, invoice, total, currency), and `ping` from the console's test button. An event the website does not take is retried after 1, 2, 4 …
up to 60 minutes, for 3 days (job `commerce.link`). Vendor console › Website link lists the last 30.

## Renewing

Administration › Subscription › **Renew** / **Add or change modules** open `https://www.deskask.in/pricing?renew=<link>` (valid 3 days). The website
asks `GET renew?token=` for the customer, their contact details and current plan, fills the checkout, and sends `renew` with it; only then may
the customer's own address be bought again. A renewal before the paid period ends adds the new period after it.

## Products on sale (6.5.1)

The platform keeps one list of products (Vendor console › Prices › Products on sale). `GET /api/link/v1/plans` only returns sizes of products that are on sale, so switching a product off in the console removes it from the website, quotes and the free trial at once. `GET /api/link/v1/products` lists every product (key, name, tagline, onSale, built, sizes); `POST /api/link/v1/products/{key}` with `{ "onSale": true|false, "name": "...", "tagline": "..." }` (signed like every other call) lets the website's admin switch a product or rename it. Every change, from either side, is sent to the website as the event `product.changed` with the product's new state, so the website can refresh what it caches. A product added in the console that the program does not contain yet (`built: false`) can be listed and sold, but customers receive it only when a release that contains it is installed.

## When the platform cannot send events to the website (6.5.4)

Vendor console › Website link › **Send a test event** now says exactly what it called and what came back, for example `The website answered 404 at https://www.deskask.in/store/hooks` with a hint:

- **Could not be reached / actively refused:** the website opens in a browser, but this server cannot reach the website's *public* address through the firewall (hairpin NAT). Fill in **Connect to** with the website server's internal address (for example `10.10.10.150`). The platform then connects to that server but still uses the name in the website address for the Host header and the certificate, so HTTPS keeps working.
- **Redirected (301/302/307/308):** the platform no longer follows redirects (a redirected POST loses its body). Set the website address to exactly where it ends up (https, with or without `www`).
- **401 or 403:** the signature was not accepted: paste the newest secret into the website and check its clock.
- **404 or 405:** the website has no event page at that path. Set **Event path** if the website uses another path than `/store/hooks`.

The website reaching the platform and the platform reaching the website are two separate network paths; the second one is what the test event checks.
