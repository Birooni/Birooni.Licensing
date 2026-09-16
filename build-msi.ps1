# build-msi.ps1: Compiles BiruBox in Release configuration and builds the Windows .MSI installer with WiX v4
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host " 1. Building BiruBox & Licensing Client ($Configuration)..." -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

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
