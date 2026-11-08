# ADR 0008: Signed licences and entitlements

Status: accepted (8 Nov 2026)

A licence is a token signed by the vendor with ES256 (header.payload.signature). Payload: customer, plan, modules with optional limits, issued, not-before, expiry, grace days. Every installation holds only the vendor public key (`Licensing:VendorPublicKeyPem`).
The same token works everywhere: stored in the catalog (`platform.licenses`, SaaS), a file (`Licensing:File`) or uploaded by an administrator (`PUT /api/platform/license`, on-prem). The valid token issued last wins, so an old licence never replaces a newer one. A licence is bound to one customer address id ("*" is for vendor demos).
Expired licences keep working for the grace period (default 14 days), then every licensed module answers 402.
Modules protect routes with `.RequireModule("assets")` and check limits with `IEntitlements.WithinLimitAsync`. Tool: `tools/DeskAsk.LicenseTool` (`keygen`, `issue`).
Development: `Licensing:AllowUnlicensed=true` opens every module.
