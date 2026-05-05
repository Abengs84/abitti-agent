param(
    [Parameter(Mandatory = $true)]
    [string]$Owner,
    [string]$Repo = "abitti-agent",
    [string]$Version = "latest",
    [string]$AssetName = "abitti-agent-client-win-x64.zip",
    [string]$InstallRoot = "C:\Program Files\AbittiAgent",
    [string]$WorkDir = "$env:TEMP\AbittiAgentInstall"
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

if (Test-Path $WorkDir) {
    Remove-Item -Path $WorkDir -Recurse -Force
}
New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null

$zipPath = Join-Path $WorkDir $AssetName
$extractPath = Join-Path $WorkDir "extract"

if ($Version -eq "latest") {
    $downloadUrl = "https://github.com/$Owner/$Repo/releases/latest/download/$AssetName"
} else {
    $downloadUrl = "https://github.com/$Owner/$Repo/releases/download/$Version/$AssetName"
}

Write-Host "Downloading client package from $downloadUrl"
Invoke-WebRequest -Uri $downloadUrl -OutFile $zipPath

New-Item -ItemType Directory -Path $extractPath -Force | Out-Null
Expand-Archive -Path $zipPath -DestinationPath $extractPath -Force

New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
Copy-Item (Join-Path $extractPath "*") $InstallRoot -Recurse -Force

$installScript = Join-Path $InstallRoot "install-client.ps1"
if (-not (Test-Path $installScript)) {
    throw "install-client.ps1 not found in package."
}

Write-Host "Running packaged installer script..."
powershell -ExecutionPolicy Bypass -File $installScript -InstallRoot $InstallRoot

Write-Host "Client install completed from GitHub release."
