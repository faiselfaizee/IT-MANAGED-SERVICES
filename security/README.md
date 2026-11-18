# DeskAsk security scanner

A self-contained tool that checks a **live DeskAsk server from the outside** for common web-security problems, shows a **live log** and a **live findings report**, and gives a **mitigation plan** for anything it finds. It is meant to be run from the public internet against your own production server.

It is **safe and read-only**: it never signs in with real passwords and never creates, changes or deletes anything. The only active step is a short burst of about 20 deliberately-wrong sign-in attempts, used to confirm the rate limit is working.

> Run this only against a server you own or are authorised to test.

## Why it is a small Node program, not just an HTML file

A page opened straight from disk can send requests to your server but **cannot read the responses** — browsers block cross-site reads (CORS), so it could not actually inspect headers, status codes or the rate limit. So the scanning is done by a tiny Node script (no `npm install`, only built-in modules), which serves the dashboard page locally. You still get exactly what was asked: a page with a live log and a live report.

## Running it

You need Node.js (already installed where you build the web app). From the repository root:

```
node security/deskask-scan.mjs
```

Then open **http://localhost:7900** in your browser, type your public DeskAsk address (for example `https://platform.deskask.in`) and press **Start scan**.

- Watch the **Live log** on the left and the **Findings & mitigation** on the right fill in as it runs.
- When it finishes, press **Download report** to save a single-file HTML report (severity summary, every finding, evidence and the fix) you can keep or send on.

Change the port with `--port`, e.g. `node security/deskask-scan.mjs --port 8085`.

## What it checks

| Area | Check |
|---|---|
| Transport | HTTPS in use, HTTP→HTTPS redirect, certificate trust and expiry, TLS version |
| Browser headers | CSP with `frame-ancestors 'none'`, `X-Content-Type-Options`, `X-Frame-Options`, HSTS, `Referrer-Policy`, `Permissions-Policy` |
| Information leaks | version/stack banner, `/health` not leaking version or roles, directory listing |
| Access control | every sampled `/api/*` route refuses anonymous callers; `/whoami` public by design is noted |
| Sign-in | rate limit active, return address cannot be an external URL (open redirect), cookie flags |
| Exposed files | `appsettings*.json`, `web.config`, `.git`, `.env`, `.pem`, `.dll` not downloadable |
| Methods | `TRACE` disabled |

Each finding carries a severity (critical / high / medium / low / info), a status (pass / warn / fail), the evidence, and a mitigation step.

## Limits

This is an **unauthenticated external scan**. It does not test logic behind sign-in, SSRF from the server, SQL injection, or anything needing a session. It complements, but does not replace, the manual review in `docs/security.md` and a full third-party penetration test.
