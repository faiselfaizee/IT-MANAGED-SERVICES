# Security of a DeskAsk installation

## What the platform does by itself
- **Sign-in:** passwords hashed (ASP.NET Identity), 10 characters at least, per-account lockout after repeated failures, optional two-step sign-in (and `security.require_2fa` to force it), Microsoft, Google and Active Directory sign-in, PKCE on every sign-in.
- **Addresses reachable without signing in** (`/account/*`, `/connect/token`, `/api/public/*`, agent and controller enrolment) are limited per address: 30 a minute (enrolment 120). Change with `Security:AuthPerMinute`. A 429 answer carries `Retry-After`.
- **Browser protections** on every response: Content-Security-Policy (own scripts only, no framing), `X-Frame-Options`, `nosniff`, `Referrer-Policy: no-referrer`, `Strict-Transport-Security` over https, and `Cache-Control: no-store` on sign-in and API answers.
- **Web hooks and Teams** may only reach public addresses. Private, loopback, link-local (cloud metadata) and carrier-grade addresses are refused after the name is resolved. A receiver inside your own network needs `DESKASK_ALLOW_PRIVATE_WEBHOOKS=1` in the service environment.
- **`/health`** gives only `ok`; the version is given to the installer and updater asking for `localhost`.
- **Secrets** (mail, LDAP, Teams, vault managed accounts, backup storage) are sealed with the server's data protection keys; vault contents are encrypted in the browser.
- **Agents and controllers** authenticate with a long random key kept only as a hash; updates are signed.
- **Releases** are checked in CI: `npm audit` for shipped web packages, `dotnet list package --vulnerable`, and a test that fails when an `/api` route has no authorization rule.

## What you should do around it
1. End TLS on a proxy or IIS with TLS 1.2 or later; send `X-Forwarded-Proto: https`; turn on HSTS there too.
2. Do not leave a firewall rule that allows everything. Forward only 443 (platform), 80 (certificate renewal) and the RustDesk ports.
3. Turn on "require two-step sign-in" for administrators and engineers (Users › two-step).
4. Keep the server patched; take a database backup and a copy of the data-protection key folder (without it sealed secrets cannot be read).
5. Give engineers the narrowest Access grants they need; use customer administrators for customers.
6. Review Audit events for `auth.login_failed` and `auth.locked`.
