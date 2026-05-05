# Abitti Agent MVP v1

This document defines a concrete MVP implementation plan for a Windows systray + remote update system for AbittiCandidate.

## 1) Scope

- Target: 10 clients in pilot, scale to 100.
- App focus: AbittiCandidate only.
- Client behavior:
  - Poll every 6 hours.
  - Prompt user when update is available.
  - Allow postpone without hard limit.
  - Support remote "check now" and remote "install now".
- Update window:
  - Default allowed install window: `16:00-06:00`.
  - Must be configurable from admin panel.
- Language:
  - Auto from Windows UI language.
  - Fallback language: Swedish.
- Logging:
  - Local logs only on clients and server.

## 2) Architecture

Client machine has 2 components:

- `AbittiAgent.Tray` (user session)
  - Systray UI, notifications, manual actions.
  - Poll schedule and command fetch.
  - Shows current status/version.
- `AbittiAgent.Service` (Windows Service, LocalSystem)
  - Executes privileged install/uninstall actions.
  - Runs `msiexec` silently.
  - Writes operation logs/status.

Server machine (local laptop):

- `AbittiAgent.Server` (REST API)
  - Receives heartbeats.
  - Stores client state and command queue.
  - Serves latest update policy.
- `AbittiAgent.AdminWeb` (simple web UI)
  - Client list and status.
  - Push "check now" / "install now".
  - Configure maintenance window and defaults.

## 3) Data Model (MVP)

### Client identity

- `clientId`: stable GUID generated on first start.
- `hostname`: from machine.
- `osVersion`: Windows version string.
- `agentVersion`: tray/service version.

### Client status

- `abittiVersionInstalled`: e.g. `1.13.0.0` or `not_installed`.
- `lastSeenUtc`
- `lastCheckUtc`
- `lastInstallUtc`
- `lastInstallResult`: `success|failed|pending|none`
- `lastError`: short string
- `pendingReboot`: bool
- `isWithinMaintenanceWindow`: bool

### Command

- `commandId`
- `clientId`
- `type`: `check_now|install_now`
- `createdUtc`
- `expiresUtc`
- `issuedBy`
- `status`: `pending|picked|done|failed`
- `resultMessage`

## 4) API Contract (MVP)

All endpoints are JSON over HTTPS in production (HTTP allowed in lab only).

### `POST /api/heartbeat`

Client -> server periodic status.

Request example:

```json
{
  "clientId": "8f66d8f6-7f59-4eec-b38e-9f7ca05f8d93",
  "hostname": "DESKTOP-01",
  "osVersion": "Windows 11 24H2",
  "agentVersion": "0.1.0",
  "abittiVersionInstalled": "1.13.0.0",
  "lastCheckUtc": "2026-05-04T12:00:00Z",
  "lastInstallUtc": "2026-05-03T20:15:12Z",
  "lastInstallResult": "success",
  "lastError": "",
  "pendingReboot": false
}
```

Response example:

```json
{
  "serverTimeUtc": "2026-05-04T12:00:02Z",
  "pollIntervalMinutes": 360,
  "maintenanceWindow": {
    "startLocal": "16:00",
    "endLocal": "06:00"
  }
}
```

### `GET /api/commands/{clientId}`

Client pulls pending commands.

Response example:

```json
{
  "commands": [
    {
      "commandId": "2f67a8d2-8d52-49bf-8b14-11737cb86b40",
      "type": "install_now",
      "expiresUtc": "2026-05-04T15:00:00Z"
    }
  ]
}
```

### `POST /api/commands/{commandId}/ack`

Client updates command status.

Request example:

```json
{
  "status": "done",
  "resultMessage": "Install exit code 0"
}
```

### `POST /api/admin/commands`

Admin creates commands for one or many clients.

Request example:

```json
{
  "targetClientIds": [
    "8f66d8f6-7f59-4eec-b38e-9f7ca05f8d93"
  ],
  "type": "check_now",
  "expiresUtc": "2026-05-04T15:00:00Z",
  "issuedBy": "it-admin"
}
```

### `GET /api/admin/clients`

Admin list view source.

### `POST /api/admin/settings`

Configure defaults.

Request example:

```json
{
  "pollIntervalMinutes": 360,
  "maintenanceWindow": {
    "startLocal": "16:00",
    "endLocal": "06:00"
  }
}
```

## 5) Installer Flow

Service-side install command:

```powershell
msiexec /i "C:\ProgramData\AbittiAgent\cache\AbittiCandidateInstaller.msi" /qn /norestart /l*v "C:\ProgramData\AbittiAgent\logs\abitti-msi.log"
```

MVP installer behavior:

1. Download MSI from `https://dl.abitti.fi/AbittiCandidateInstaller.msi` if remote version differs.
2. Run silent install.
3. Capture exit code and update status.
4. Persist log path + result for tray and admin panel.

Exit code handling:

- `0`: success
- `3010`: success, reboot recommended
- other: fail with error details

## 6) Client State Machine (MVP)

- `Idle`
- `CheckingVersion`
- `UpdateAvailable`
- `WaitingForUserDecision`
- `ScheduledForWindow`
- `Installing`
- `InstallSucceeded`
- `InstallFailed`

Triggers:

- Poll tick.
- Remote command (`check_now|install_now`).
- User click (`install now|later`).
- Window opens.

## 7) Localization

Language selection:

1. Windows UI language (`sv`, `fi`, `en`).
2. Fallback to Swedish (`sv`).

Minimum strings:

- Update available
- Install now
- Remind later
- Installing
- Installed successfully
- Installation failed
- Open logs

## 8) Logging

Client paths:

- `C:\ProgramData\AbittiAgent\logs\agent.log`
- `C:\ProgramData\AbittiAgent\logs\service.log`
- `C:\ProgramData\AbittiAgent\logs\abitti-msi.log`

Server path:

- `C:\ProgramData\AbittiAgentServer\logs\server.log`

MVP retention:

- Keep last 30 days or max 50 MB per log, whichever comes first.

## 9) Security (MVP baseline)

- Use API key between clients and server (rotateable).
- Commands must be fetched by client pull (no inbound firewall exceptions needed on clients).
- Validate that MSI source URL matches allowed list (`dl.abitti.fi`).
- Optional later: verify Authenticode signature and hash.

## 10) Deployment Plan

Phase 1 (pilot 10 clients):

1. Install server on local laptop.
2. Install client tray + service on 10 pilot machines.
3. Verify heartbeat, check-now, install-now flows.
4. Validate logs and user prompt behavior.

Phase 2 (scale to 100):

1. Bulk deploy client installer.
2. Tune polling and server resources.
3. Add backup and optional AD/Intune integration.

## 11) Implementation Backlog

P0:

- Client heartbeat/poll loop.
- Service install execution.
- Admin list + command send.
- Maintenance window handling.

P1:

- Better retry/backoff.
- Richer status history in panel.
- Optional "quiet only outside class hours" profile presets.

P2:

- AD/Intune-aware deployment.
- Signature/hash verification.
- Alerting and reports.

## 12) Open Questions to lock before coding

- Exact allowed install behavior for `install_now` if outside maintenance window:
  - Force immediately, or still respect window?
- Should tray show install progress percent or only states?
- Should users be able to disable prompts locally (admin override)?
