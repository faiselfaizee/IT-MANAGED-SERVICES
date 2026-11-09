# ADR 0009: Signed release bundles and a separate updater

Status: accepted (8 Nov 2026)

- The platform never replaces its own files. A separate service, `deskask-updater`, does, and it is the only one with that right.
- A release is a **bundle** (`.dkb`): `payload/` files, `manifest.json` (path, size, SHA-256, mode of every file) and `manifest.sig` (ES256 signature of the manifest by the release key). GitHub Actions makes and signs bundles on a version tag (`release.yml`); the private key is only the repository secret `RELEASE_SIGNING_KEY`.
- The updater trusts one or more release public keys (`-pub`), verifies the signature itself, refuses extra, missing, changed or unsafe (`..`, absolute, backslash) files, extracts to a staging folder and hashes every file against the manifest, **before** it stops the service.
- Swap: stop, `live` becomes `previous`, staging becomes `live`, start, wait for the health address. If start or health fails, the old version is put back and must answer again; otherwise the result is "rollback failed: needs attention".
- Settings and data survive: `-keep appsettings.Production.json,data` are carried over from the old `live` and win over the bundle's files.
- Only newer versions (semantic versioning, pre-releases included) are installed; re-applying needs `force`, going back needs `force` and `allow-downgrade`.
- The platform's part is small: `POST /api/platform/updates` (administrator **and** signed in with two-step sign-in) drops the file in `inbox/<id>/` (`job.json` last). The updater picks it up, writes `state/jobs/<id>.json` and `state/status.json`; `GET /api/platform/updates` shows them.
- Release keys can be rotated: the updater accepts several public keys in one file.
