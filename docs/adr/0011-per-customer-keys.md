# ADR 0011: Per-customer signing and encryption keys

Status: accepted (9 Nov 2026). Completes ADR 0006.

## Decision
- Each customer has its own ES256 signing key. It is made on first use and kept in the customer's database (`identity.keys`), sealed with AES-GCM under a key made from `Identity:EncryptionSecret`.
- The key that protects refresh tokens and authorization codes is made from the same secret and the customer id, so one customer's tokens cannot be opened by another.
- OpenIddict reads its options on every request. `TenantOptionsMonitor` gives it a copy per customer with that customer's keys, so one process serves many customers.
- Public keys are published per customer at `/.well-known/jwks` (and `/.well-known/openid-configuration`). Only the public half is ever shown.
- `POST /api/platform/identity/keys/rotate` (admin, audited) starts a new key. The old key stays in the published list for two days, so tokens signed just before still verify. Other processes pick up a rotation within 5 minutes. Retired keys are deleted after 30 days by the `identity.prune` job.
- `Identity:SigningKeyPem` still works as one fixed key for every customer (older setup). Rotation is then done in configuration.

## Consequences
- A leaked key harms one customer. Moving a customer database moves its key; the encryption secret must move with it.
- Changing `Identity:EncryptionSecret` makes stored keys unreadable (and breaks two-step secrets). Back it up and never change it.
- Tokens and sessions made before this change are not valid after it (alpha only).
