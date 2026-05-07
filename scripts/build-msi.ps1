param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "0.1.0",
    [string]$OutputDir = "artifacts\msi"
)

$ErrorActionPreference = "Stop"
$wixVersion = "5.0.2"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$stagingDir = Join-Path $repoRoot "artifacts\msi-staging"
$outputRoot = Join-Path $repoRoot $OutputDir
$payloadDir = Join-Path $stagingDir "payload"
$wxsPath = Join-Path $repoRoot "installer\AbittiAgent.Installer.wxs"
$iconPath = Join-Path $repoRoot "assets\AbittiAgent.ico"
$msiPath = Join-Path $outputRoot "AbittiAgent-$Version-$Runtime.msi"
$msiLatestPath = Join-Path $outputRoot "AbittiAgent-latest-$Runtime.msi"

Write-Host "Preparing folders..."
if (Test-Path $stagingDir) { Remove-Item -Path $stagingDir -Recurse -Force }
New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

Write-Host "Publishing service..."
dotnet publish (Join-Path $repoRoot "src\AbittiAgent.Service\AbittiAgent.Service.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:Version=$Version `
    -p:FileVersion=$Version `
    -p:InformationalVersion=$Version `
    -o (Join-Path $stagingDir "service")

Write-Host "Publishing tray..."
dotnet publish (Join-Path $repoRoot "src\AbittiAgent.Tray\AbittiAgent.Tray.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:Version=$Version `
    -p:FileVersion=$Version `
    -p:InformationalVersion=$Version `
    -o (Join-Path $stagingDir "tray")

Copy-Item (Join-Path $stagingDir "service\AbittiAgent.Service.exe") $payloadDir -Force
Copy-Item (Join-Path $stagingDir "tray\AbittiAgent.Tray.exe") $payloadDir -Force
Copy-Item (Join-Path $stagingDir "service\appsettings.json") $payloadDir -Force

$wixCmd = Get-Command wix -ErrorAction SilentlyContinue
if ($wixCmd) {
    $installedVersion = wix --version
    if ($installedVersion -like "7.*") {
        Write-Host "Replacing WiX v7 with WiX $wixVersion..."
        dotnet tool uninstall --global wix | Out-Null
        $wixCmd = $null
    }
}

if (-not $wixCmd) {
    Write-Host "Installing wix tool..."
    dotnet tool install --global wix --version $wixVersion
    $env:PATH += ";$env:USERPROFILE\.dotnet\tools"
    $wixCmd = Get-Command wix -ErrorAction SilentlyContinue
    if (-not $wixCmd) {
        throw "wix CLI not found after installation."
    }
}

Write-Host "Building MSI..."
wix extension add "WixToolset.Util.wixext/$wixVersion" | Out-Null
wix build $wxsPath `
    -arch x64 `
    -ext WixToolset.Util.wixext `
    -d PayloadDir="$payloadDir" `
    -d IconPath="$iconPath" `
    -d Version="$Version" `
    -o $msiPath

Copy-Item $msiPath $msiLatestPath -Force

Write-Host "MSI built: $msiPath"
