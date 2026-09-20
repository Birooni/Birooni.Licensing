# build-msi.ps1: Compiles BiruBox in Release configuration and builds the Windows .MSI installer with WiX v4
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host " 1. Building BiruBox & Licensing Client ($Configuration)..." -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

dotnet build "$root\BiruBox.Loader\BiruBox.Loader.csproj" -c $Configuration
dotnet build "$root\BiruBox\BiruBox.csproj" -c $Configuration

Write-Host "`n==================================================" -ForegroundColor Cyan
Write-Host " 2. Building BiruBox Windows Installer (.MSI)..." -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

$installerDir = "$root\BiruBox.Installer"
$outputMsi = "$installerDir\BiruBox-Setup.msi"

Push-Location $installerDir
try {
    wix build "Package.wxs" -arch x64 -out "BiruBox-Setup.msi"
} finally {
    Pop-Location
}

if (Test-Path $outputMsi) {
    $item = Get-Item $outputMsi
    $sizeKb = [math]::Round($item.Length / 1KB, 2)
    Write-Host "`n[SUCCESS] Installer built successfully!" -ForegroundColor Green
    Write-Host "Output MSI: $outputMsi ($sizeKb KB)" -ForegroundColor Yellow
} else {
    Write-Error "MSI build failed. File not found at $outputMsi"
}

Write-Host "`n==================================================" -ForegroundColor Cyan
Write-Host " 3. Packaging BiruBox-update.zip for In-Revit Auto-Updater..." -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

$binDir = "$root\BiruBox\bin\$Configuration"
$updateZip = "$installerDir\BiruBox-update.zip"
$webDownloads = "$root\..\website\downloads"

if (Test-Path $updateZip) { Remove-Item $updateZip -Force }

$zipItems = @(
    (Join-Path $binDir "BiruBox.dll"),
    (Join-Path $binDir "Birooni.Client.dll"),
    (Join-Path $binDir "System.Management.dll"),
    (Join-Path $binDir "BiruBox.deps.json")
)
if (Test-Path (Join-Path $binDir "runtimes")) {
    $zipItems += (Join-Path $binDir "runtimes")
}

Compress-Archive -Path $zipItems -DestinationPath $updateZip -Force

if (Test-Path $webDownloads) {
    Copy-Item $outputMsi (Join-Path $webDownloads "BiruBox-Setup.msi") -Force
    Copy-Item $updateZip (Join-Path $webDownloads "BiruBox-update.zip") -Force
    Write-Host "[SUCCESS] Deployed BiruBox-Setup.msi and BiruBox-update.zip to $webDownloads" -ForegroundColor Green
}
