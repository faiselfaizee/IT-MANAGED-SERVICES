# Releasing and updating

## One time (vendor side)
1. `deskask-release keygen -out keys` (build it with `cd updater && go build ./cmd/deskask-release`).
2. Store `keys/release-private.pem` as the GitHub repository secret **RELEASE_SIGNING_KEY**. Keep an offline copy. Never commit it.
3. `keys/release-public.pem` goes to every server (each release also attaches it).

## Making a release
`git tag v2.0.1 && git push origin v2.0.1`. The `release` workflow tests everything, builds the platform for Linux (needs the .NET runtime) and Windows (self-contained), adds the web app as `wwwroot`, signs one bundle per platform, and publishes them with the updater, the agents and `SHA256SUMS.txt`. Tags containing `-` (for example `v2.1.0-beta.1`) are pre-releases.

## One time (each server)
1. Put `deskask-updater` and `release-public.pem` on the server. Pick a root folder, for example `/opt/deskask` or `C:\DeskAsk`.
2. Run the platform from `<root>/live` as a service. Linux: `deploy/updater/deskask-updater.service` and `deskask-platform.service`. The first version is installed with `deskask-updater apply -root <root> -pub release-public.pem -bundle <file.dkb> -stop-cmd ... -start-cmd ... -health http://localhost:5000/health`.
3. Run the updater as a service: `deskask-updater run -root <root> -pub release-public.pem -stop-cmd "systemctl stop deskask-platform" -start-cmd "systemctl start deskask-platform" -health http://localhost:5000/health`.
4. Give the platform `Updates:Dir=<root>` and let its user write to `<root>/inbox` only. The platform user must **not** be able to write `live`, `previous` or the public key.

## Updating
- From the platform: an administrator who signed in with two-step sign-in uploads the `.dkb` (`POST /api/platform/updates?name=...`). The updater applies it within seconds.
- By hand: `deskask-updater apply ... -bundle <file.dkb>`; go back to the previous version with `deskask-updater rollback ...`.

## What protects a server
A bundle that was not signed by a trusted release key, was changed after signing, or contains anything the manifest does not list is refused before anything is stopped. A new version that does not start or does not answer its health address is replaced by the old one automatically.
