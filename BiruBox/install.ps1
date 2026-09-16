# Copies BiruBox.addin + binaries into Revit Addins folders.
# Run from the build output folder (bin/Release) after: dotnet build -c Release
param(
    [int[]]$Years = @(2025, 2026, 2027)
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$dll = Join-Path $here "BiruBox.dll"
$addin = Join-Path $here "BiruBox.addin"
$clientDll = Join-Path $here "Birooni.Client.dll"
$mgmtDll = Join-Path $here "System.Management.dll"
$depsJson = Join-Path $here "BiruBox.deps.json"
$runtimesDir = Join-Path $here "runtimes"

if (-not (Test-Path $dll)) {
    Write-Error "BiruBox.dll not found next to this script. Build the project first (dotnet build -c Release)."
}

foreach ($year in $Years) {
    $addinDest = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year"
    $binDest = Join-Path $addinDest "Birooni Tools\BiruBox"

    New-Item -ItemType Directory -Force -Path $binDest | Out-Null
    
    # Copy manifest into Addins\{Year}
    Copy-Item $addin (Join-Path $addinDest "BiruBox.addin") -Force

    # Copy DLLs & dependencies into Birooni Tools\BiruBox
    Copy-Item $dll (Join-Path $binDest "BiruBox.dll") -Force
    if (Test-Path $clientDll) { Copy-Item $clientDll (Join-Path $binDest "Birooni.Client.dll") -Force }
    if (Test-Path $mgmtDll) { Copy-Item $mgmtDll (Join-Path $binDest "System.Management.dll") -Force }
    if (Test-Path $depsJson) { Copy-Item $depsJson (Join-Path $binDest "BiruBox.deps.json") -Force }
    if (Test-Path $runtimesDir) { Copy-Item $runtimesDir $binDest -Recurse -Force }

    Write-Host "Installed BiruBox for Revit $year -> $binDest"
}

Write-Host "`nInstallation completed successfully. Restart Revit. Ribbon: Add-Ins > BiruBox"

