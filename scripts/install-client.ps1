param(
    [string]$ServiceName = "AbittiAgentService",
    [string]$InstallRoot = "C:\Program Files\AbittiAgent",
    [string]$ServiceExe = "AbittiAgent.Service.exe",
    [string]$TrayExe = "AbittiAgent.Tray.exe"
)

$ErrorActionPreference = "Stop"

function Ensure-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($id)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script as Administrator."
    }
}

Ensure-Admin

$servicePath = Join-Path $InstallRoot $ServiceExe
$trayPath = Join-Path $InstallRoot $TrayExe

if (-not (Test-Path $servicePath)) { throw "Service exe not found: $servicePath" }
if (-not (Test-Path $trayPath)) { throw "Tray exe not found: $trayPath" }

Write-Host "Configuring Windows service $ServiceName..."

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    sc.exe stop $ServiceName | Out-Null
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 1
}

sc.exe create $ServiceName binPath= "`"$servicePath`"" start= auto DisplayName= "Abitti Agent Service" | Out-Null
sc.exe description $ServiceName "Runs silent Abitti updates in background." | Out-Null
sc.exe start $ServiceName | Out-Null

Write-Host "Configuring Tray autostart for all users..."
$runKey = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Run"
New-ItemProperty -Path $runKey -Name "AbittiAgentTray" -PropertyType String -Value "`"$trayPath`"" -Force | Out-Null

Write-Host "Done."
Write-Host "Service: $ServiceName (Automatic)"
Write-Host "Tray autostart: HKLM Run -> $trayPath"
