# Abitti Agent Scaffold

This folder contains a starter scaffold for the Abitti Agent MVP defined in `docs/AbittiAgent-MVP.md`.

## Projects

- `AbittiAgent.Shared` - shared contracts/models.
- `AbittiAgent.Service` - privileged Windows Service installer engine.
- `AbittiAgent.Server` - local admin API backend + built-in admin web UI (`/`).
- `AbittiAgent.Tray` - user systray app (Windows).

## Prerequisites

- .NET 10 SDK/runtime
- Windows 10/11 (for service/tray)

## Build (after installing .NET SDK)

```powershell
cd abitti-agent
dotnet build
```

## Run (single EXE for admin)

Run only `AbittiAgent.Server.exe` and open `http://127.0.0.1:5188/` in a browser.
The page includes the admin client list and the API endpoints.

## Zero-click install model

- `AbittiAgent.Tray` no longer runs MSI directly.
- Tray calls local `AbittiAgent.Service` API at `http://127.0.0.1:38181/`.
- Service performs silent MSI install in background (`msiexec /qn`) as the service account.
- For true zero-click updates, `AbittiAgent.Service` must be installed and running on each client.

## Client autostart setup

Yes, zero-click mode uses two processes on each client:
- `AbittiAgent.Service` (Windows service, auto-start)
- `AbittiAgent.Tray` (user tray app at logon)

Use:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install-client.ps1
```

The script registers:
- Service `AbittiAgentService` as Automatic startup
- Tray autostart in `HKLM\Software\Microsoft\Windows\CurrentVersion\Run`

## GitHub release package (single file)

The repository includes a GitHub Actions workflow:
- `.github/workflows/release-client-package.yml`

It builds one MSI package per release (and a fallback zip) containing:
- `AbittiAgent.Service.exe`
- `AbittiAgent.Tray.exe`
- `appsettings.json` (service default)
- `install-client.ps1`

### Publish package to Releases

- Push a tag like `v0.1.0`, or run the workflow manually.
- The workflow creates a GitHub Release and uploads:
  - `AbittiAgent-<version>-win-x64.msi`
  - `abitti-agent-client-win-x64.zip`

### Build MSI locally

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-msi.ps1 -Version 0.1.0
```

MSI output:
- `artifacts\msi\AbittiAgent-0.1.0-win-x64.msi`

### Install from GitHub Releases (client machine)

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install-client-from-github.ps1 -Owner Abengs84 -Repo abitti-agent -Version latest
```

Use `-Version v0.1.0` to install a specific release.

## Client auto-discovery (LAN)

- `AbittiAgent.Server` responds to UDP discovery on port `51880` by default.
- Set Tray `AbittiAgent:ServerBaseUrl` to `auto` (default) to discover server automatically.
- You can override with explicit URL, e.g. `http://10.0.0.15:5188`.
- If needed, set `Discovery:AdvertisedUrl` on server to force a specific address.

## Next implementation steps

1. Implement heartbeat polling in `AbittiAgent.Tray`.
2. Implement install executor in `AbittiAgent.Service`.
3. Add command queue storage in `AbittiAgent.Server`.
4. Add client actions (check/install now) in the built-in Server admin UI.
