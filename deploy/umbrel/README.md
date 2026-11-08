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
  - If SQL Server runs on the same Umbrel, enter host `127.0.0.1` (or `umbrel.local`) and its host port in the wizard.
    Containers cannot resolve mDNS (`*.local`) names, so the app maps `umbrel.local` to `127.0.0.1` (`extra_hosts`);
    other `*.local` names do not work, and the wizard says so. Use an IP address for SQL Server elsewhere.
  - The wizard creates the database when the login has `dbcreator`. Otherwise it shows the T-SQL to run, which creates
    the database plus a `ControlHome_App` login that owns only it. That least-privilege login is the recommended setup.
- Hardened the same way as `deploy/docker-compose.yml`: read-only root filesystem, tmpfs `/tmp`, all capabilities
  dropped, and `no-new-privileges`.

## Where it is published

The owner's store [RicardoZambon/umbrel-store](https://github.com/RicardoZambon/umbrel-store) (store id `zambon`)
carries the app as `zambon-controlhome`. In umbrelOS, open **App Store → ⋯ → Community App Stores**, add
`https://github.com/RicardoZambon/umbrel-store` and install **ControlHome**. `store/` here also works as a standalone
store with the id `controlhome`.

The app's icon is `icon.svg` and its gallery is `gallery/` (screenshots of the demo home, 1440 × 900), both served from
`main` of this repository.

## Releasing a new version

1. Write the `releaseNotes` in `store/controlhome-app/umbrel-app.yml` and merge it.
2. Tag `vX.Y.Z`. `release.yml` publishes `ghcr.io/ricardozambon/controlhome:X.Y.Z`, then its *Publish to the Umbrel
   store* job copies `store/controlhome-app/` to `zambon-controlhome/` in the store, sets the id and version, pins the
   image by its digest and pushes to the store's default branch. Umbrel offers the update on its next store refresh.

The job needs the repository secret `UMBREL_STORE_TOKEN`: a fine-grained personal access token with access to
`RicardoZambon/umbrel-store` only and the permission *Contents: Read and write*. Without it, the job warns and the store
is left alone.
