# ControlHome on Umbrel

`store/` is a complete [Umbrel Community App Store](https://github.com/getumbrel/umbrel-community-app-store) with one
app, `controlhome-app`. Umbrel installs apps from a git repository, so the store lives in its own public repository and
this folder is its source.

## How the app runs on Umbrel

- **Host networking**, like Umbrel's Home Assistant app: device discovery, Wake-on-LAN and device push events need the LAN.
  There is no `app_proxy` in front of it. ControlHome has its own sign-in, and Umbrel opens `http://umbrel.local:8711`.
- **Port 8711**. No app in `getumbrel/umbrel-apps` used it when it was chosen; 8099 was taken by Wavelog.
- **Setup code**: `Setup__Code=${APP_PASSWORD}`, together with `deterministicPassword: true`. The code the first-run wizard
  asks for is the app password that Umbrel shows for ControlHome.
- **Data**: `${APP_DATA_DIR}/data` is mounted at `/data`, owned by `1000:1000`. It holds the Data Protection key
  certificate (without it the stored secrets cannot be read), the encrypted database settings, uploads and backups.
- **Database**: SQL Server 2025 is required, and it is not bundled because its images are x64-only.
  - If SQL Server runs on the same Umbrel, enter host `127.0.0.1` and its host port in the wizard. On host networking,
    `umbrel.local` and other `*.local` names may not resolve inside containers.
  - The wizard creates the database when the login has `dbcreator`. Otherwise it shows the T-SQL to run, which creates
    the database plus a `ControlHome_App` login that owns only it. That least-privilege login is the recommended setup.
- Hardened the same way as `deploy/docker-compose.yml`: read-only root filesystem, tmpfs `/tmp`, all capabilities
  dropped, and `no-new-privileges`.

## Publishing the store (one-time, by the repository owner)

1. Create a public repository, for example `RicardoZambon/controlhome-umbrel-store`.
2. Copy the contents of `deploy/umbrel/store/` to its root: `umbrel-app-store.yml` and `controlhome-app/`.
3. Check that the image line in `controlhome-app/docker-compose.yml` names the release you want, pinned by digest. Get a digest with
   `docker buildx imagetools inspect ghcr.io/ricardozambon/controlhome:<version>`; the digest is on the `Digest:` line.
4. In umbrelOS, open **App Store → ⋯ → Community App Stores**, paste the repository URL, and install **ControlHome**.

## Releasing a new version

1. Tag `vX.Y.Z` in this repository; `release.yml` publishes `ghcr.io/ricardozambon/controlhome:X.Y.Z`.
2. In the store repository, edit `controlhome-app`:
   - in `docker-compose.yml`, set the image to `X.Y.Z@sha256:<digest>`;
   - in `umbrel-app.yml`, bump `version` and write the `releaseNotes`.
3. Commit. Umbrel offers the update on its next store refresh.

Automating step 2, with release.yml opening the bump PR on the store repository, needs a token with write access to that
repository. That is listed under *Needs you* in `docs/STATUS.md`.
