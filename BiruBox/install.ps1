# Copies BiruBox.addin + BiruBox.dll into Revit Addins folders.
# Run from the build output folder (bin/Release) after: dotnet build -c Release
param(
    [int[]]$Years = @(2024, 2025, 2026)
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$dll = Join-Path $here "BiruBox.dll"
$addin = Join-Path $here "BiruBox.addin"

if (-not (Test-Path $dll)) {
    Write-Error "BiruBox.dll not found next to this script. Build the project first (dotnet build -c Release)."
}

foreach ($year in $Years) {
    $revit = Join-Path ${env:ProgramFiles} "Autodesk\Revit $year\Revit.exe"
    if (-not (Test-Path $revit)) { continue }
    $dest = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year"
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item $dll (Join-Path $dest "BiruBox.dll") -Force
    Copy-Item $addin (Join-Path $dest "BiruBox.addin") -Force
    Write-Host "Installed BiruBox for Revit $year -> $dest"
}

Write-Host "Restart Revit. Ribbon: Add-Ins > BiruBox"
