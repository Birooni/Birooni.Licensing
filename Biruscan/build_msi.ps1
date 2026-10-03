param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
Set-Location $Root

function Resolve-BinDir([string]$ProjectFolder) {
    $candidates = @(
        (Join-Path $ProjectFolder "bin\x64\$Configuration"),
        (Join-Path $ProjectFolder "bin\$Configuration"),
        (Join-Path $ProjectFolder "bin\x64\Debug"),
        (Join-Path $ProjectFolder "bin\Debug")
    )
    foreach ($c in $candidates) {
        $dll = Join-Path $c "Biruscan.dll"
        if (Test-Path $dll) { return $c }
    }
    return $null
}

function Copy-Payload {
    param(
        [string]$BinDir,
        [string]$DestDir,
        [string]$AddinPath,
        [bool]$IncludeAddin = $true
    )

    New-Item -ItemType Directory -Force -Path $DestDir | Out-Null
    if ($IncludeAddin) {
        $addinDestDir = if ((Split-Path $DestDir -Leaf) -eq "Biruscan") { Split-Path $DestDir -Parent } else { $DestDir }
        Copy-Item $AddinPath -Destination (Join-Path $addinDestDir "Biruscan.addin") -Force
    }

    $skipDir = @("android", "ios", "linux-arm64", "linux-x64", "osx-arm64", "osx-x64", "win-arm64", "runtimes")
    $skipExt = @(".pdb", ".lib", ".so", ".dylib", ".aar", ".xml", ".zip")

    Get-ChildItem -Path $BinDir -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($BinDir.Length).TrimStart("\", "/")
        $parts = $rel -split "[\\/]"
        $skip = $false
        foreach ($part in $parts) { if ($skipDir -contains $part) { $skip = $true } }
        if ($skip) { return }
        if ($skipExt -contains $_.Extension.ToLowerInvariant()) { return }
        if ($_.Name -like "*.onnx.data") { return }
        if ($_.Name -like "*.Loader.dll") { return }
        if ($_.Extension -eq ".addin") { return }

        $target = Join-Path $DestDir $rel
        $parent = Split-Path $target -Parent
        if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
        Copy-Item $_.FullName -Destination $target -Force
    }
}

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host " 1. Building Biruscan ($Configuration)..." -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

dotnet build "$Root\Biruscan.Loader\Biruscan.Loader.csproj" -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Biruscan.Loader build failed" }
$loaderNet48 = "$Root\Biruscan.Loader\bin\$Configuration\net48\Biruscan.Loader.dll"
$loaderNet8 = "$Root\Biruscan.Loader\bin\$Configuration\net8.0-windows\Biruscan.Loader.dll"
if (-not (Test-Path $loaderNet48)) { throw "Missing $loaderNet48" }
if (-not (Test-Path $loaderNet8)) { throw "Missing $loaderNet8" }

$built2023 = $false
$built2025 = $false
try {
    dotnet build "$Root\2023\Biruscan.csproj" -c $Configuration -p:Platform=x64
    if ($LASTEXITCODE -eq 0) { $built2023 = $true }
} catch { Write-Host "[WARN] 2023 $Configuration build skipped: $_" -ForegroundColor Yellow }

try {
    dotnet build "$Root\2025\Biruscan.csproj" -c $Configuration -p:Platform=x64
    if ($LASTEXITCODE -eq 0) { $built2025 = $true }
} catch { Write-Host "[WARN] 2025 $Configuration build skipped: $_" -ForegroundColor Yellow }

$bin2023 = Resolve-BinDir (Join-Path $Root "2023")
$bin2025 = Resolve-BinDir (Join-Path $Root "2025")
if (-not $bin2023) { throw "No 2023 Biruscan.dll found. Build the 2023 project first." }
if (-not $bin2025) { throw "No 2025 Biruscan.dll found. Build the 2025 project first." }

Write-Host "Using 2023 payload: $bin2023  (built=$built2023)"
Write-Host "Using 2025 payload: $bin2025  (built=$built2025)"

Write-Host "`nInstalling WiX UI Extension..."
if (Test-Path ".wix") { Remove-Item ".wix" -Recurse -Force }
try { wix extension add WixToolset.UI.wixext/4.0.5 } catch {}

Write-Host "`nSetting up Source Directories..."
$SourceDir = Join-Path $Root "SourceDir"
if (Test-Path $SourceDir) { Remove-Item $SourceDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $SourceDir | Out-Null

$versions = @(
    @{ Name = "2020"; Source = "2023"; Bin = $bin2023 },
    @{ Name = "2021"; Source = "2023"; Bin = $bin2023 },
    @{ Name = "2022"; Source = "2023"; Bin = $bin2023 },
    @{ Name = "2023"; Source = "2023"; Bin = $bin2023 },
    @{ Name = "2024"; Source = "2023"; Bin = $bin2023 },
    @{ Name = "2025"; Source = "2025"; Bin = $bin2025 },
    @{ Name = "2026"; Source = "2025"; Bin = $bin2025 },
    @{ Name = "2027"; Source = "2025"; Bin = $bin2025 }
)

foreach ($v in $versions) {
    $verDir = Join-Path $SourceDir $v.Name
    $appDir = Join-Path $verDir "Biruscan"
    New-Item -ItemType Directory -Force -Path $appDir | Out-Null
    $addin = Join-Path $Root "$($v.Source)\Biruscan.addin"
    Copy-Payload -BinDir $v.Bin -DestDir $appDir -AddinPath $addin
    $loader = if ([int]$v.Name -le 2024) { $loaderNet48 } else { $loaderNet8 }
    Copy-Item $loader (Join-Path $appDir "Biruscan.Loader.dll") -Force

    # Release output may omit pretrained .dat; fall back to Debug copies.
    foreach ($datName in @("BiruscanMepWeights.dat", "BiruscanMepWeights.base.dat")) {
        $destDat = Join-Path $appDir $datName
        if (Test-Path $destDat) { continue }
        $fallbacks = @(
            (Join-Path $Root "$($v.Source)\bin\x64\Debug\$datName"),
            (Join-Path $Root "$($v.Source)\bin\Debug\$datName")
        )
        foreach ($fb in $fallbacks) {
            if (Test-Path $fb) { Copy-Item $fb $destDat -Force; break }
        }
    }
}

Write-Host "`nGenerating Components.wxs..."

$xml = @"
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Fragment>
    <SetDirectory Id="ADDINROOTFOLDER" Action="SetRootPerMachine" Value="[CommonAppDataFolder]Autodesk" Condition="INSTALL_SCOPE_SELECTION=&quot;2&quot;" />
    <SetDirectory Id="ADDINROOTFOLDER" Action="SetRootPerUser" Value="[AppDataFolder]Autodesk" Condition="INSTALL_SCOPE_SELECTION=&quot;1&quot;" />

    <StandardDirectory Id="TARGETDIR">
      <Directory Id="ADDINROOTFOLDER" Name="Autodesk">
        <Directory Id="REVITFOLDER" Name="Revit">
          <Directory Id="ADDINSFOLDER" Name="Addins">
"@

$script:compCounter = 1
$features = ""
$compGroups = ""

function Get-WixDirTree {
    param($Path, $Indent, [ref]$compList, $VerName)
    $out = ""
    $items = Get-ChildItem -Path $Path

    foreach ($file in $items | Where-Object { -not $_.PSIsContainer }) {
        $compId = "Comp_$script:compCounter"
        $script:compCounter++
        $compList.Value += $compId
        $guid = [guid]::NewGuid().ToString()
        $rel = $file.FullName.Substring($Root.Length).TrimStart("\", "/")
        $out += "$Indent<Component Id=`"$compId`" Guid=`"$guid`" Condition=`"REVIT_$VerName = &quot;1&quot;`">`n"
        $out += "$Indent  <File Source=`"$rel`" KeyPath=`"yes`" />`n"
        $out += "$Indent</Component>`n"
    }

    foreach ($dir in $items | Where-Object { $_.PSIsContainer }) {
        $out += "$Indent<Directory Name=`"$($dir.Name)`">`n"
        $out += Get-WixDirTree -Path $dir.FullName -Indent "$Indent  " -compList $compList -VerName $VerName
        $out += "$Indent</Directory>`n"
    }
    return $out
}

foreach ($v in $versions) {
    $verName = $v.Name
    $xml += "            <Directory Id=`"DIR_REVIT_$verName`" Name=`"$verName`">`n"

    $verList = @()
    $xml += Get-WixDirTree -Path (Join-Path $SourceDir $verName) -Indent "              " -compList ([ref]$verList) -VerName $verName

    $xml += "            </Directory>`n"

    $features += "    <Feature Id=`"Feature_$verName`" Title=`"Revit $verName`" Level=`"1`">`n"
    $features += "      <ComponentGroupRef Id=`"Group_$verName`" />`n"
    $features += "    </Feature>`n"

    $compGroups += "    <ComponentGroup Id=`"Group_$verName`">`n"
    foreach ($c in $verList) {
        $compGroups += "      <ComponentRef Id=`"$c`" />`n"
    }
    $compGroups += "    </ComponentGroup>`n"
}

$xml += @"
          </Directory>
        </Directory>
      </Directory>
    </StandardDirectory>

$compGroups

  </Fragment>

  <Fragment>
    <FeatureGroup Id="AllFeatures">
$features
    </FeatureGroup>
  </Fragment>
</Wix>
"@

Set-Content -Path (Join-Path $Root "Components.wxs") -Value $xml -Encoding UTF8

$outputDir = Join-Path $Root "Output"
if (-not (Test-Path $outputDir)) { New-Item -ItemType Directory -Path $outputDir | Out-Null }

Write-Host "`n==================================================" -ForegroundColor Cyan
Write-Host " 2. Building Biruscan MSI Installer..." -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

wix build Installer.wxs Components.wxs CustomUI.wxs -ext WixToolset.UI.wixext -out (Join-Path $outputDir "Biruscan-Setup.msi")
if ($LASTEXITCODE -ne 0) { throw "WiX build failed" }

$msi = Join-Path $outputDir "Biruscan-Setup.msi"
$sizeKb = [math]::Round((Get-Item $msi).Length / 1KB, 1)
Write-Host "[SUCCESS] $msi ($sizeKb KB)" -ForegroundColor Green

Write-Host "`n==================================================" -ForegroundColor Cyan
Write-Host " 3. Packaging Biruscan-update.zip..." -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

$updateZip = Join-Path $outputDir "Biruscan-update.zip"
$updateZipNet48 = Join-Path $outputDir "Biruscan-update-net48.zip"
if (Test-Path $updateZip) { Remove-Item $updateZip -Force }
if (Test-Path $updateZipNet48) { Remove-Item $updateZipNet48 -Force }
$zipStage = Join-Path $env:TEMP "biruscan-update-stage"
$zipStage48 = Join-Path $env:TEMP "biruscan-update-stage-net48"
if (Test-Path $zipStage) { Remove-Item $zipStage -Recurse -Force }
if (Test-Path $zipStage48) { Remove-Item $zipStage48 -Recurse -Force }
Copy-Payload -BinDir $bin2025 -DestDir $zipStage -AddinPath (Join-Path $Root "2025\Biruscan.addin") -IncludeAddin $false
Copy-Payload -BinDir $bin2023 -DestDir $zipStage48 -AddinPath (Join-Path $Root "2023\Biruscan.addin") -IncludeAddin $false
foreach ($stage in @($zipStage, $zipStage48)) {
    $srcYear = if ($stage -eq $zipStage) { "2025" } else { "2023" }
    foreach ($datName in @("BiruscanMepWeights.dat", "BiruscanMepWeights.base.dat")) {
        $destDat = Join-Path $stage $datName
        if (Test-Path $destDat) { continue }
        foreach ($fb in @(
            (Join-Path $Root "$srcYear\bin\x64\Debug\$datName"),
            (Join-Path $Root "$srcYear\bin\Debug\$datName")
        )) {
            if (Test-Path $fb) { Copy-Item $fb $destDat -Force; break }
        }
    }
}
Compress-Archive -Path (Join-Path $zipStage "*") -DestinationPath $updateZip -Force
Compress-Archive -Path (Join-Path $zipStage48 "*") -DestinationPath $updateZipNet48 -Force
Remove-Item $zipStage -Recurse -Force
Remove-Item $zipStage48 -Recurse -Force
Write-Host "[SUCCESS] $updateZip" -ForegroundColor Green
Write-Host "[SUCCESS] $updateZipNet48" -ForegroundColor Green

$webDownloads = Join-Path $Root "..\website\downloads"
if (Test-Path $webDownloads) {
    Copy-Item $msi (Join-Path $webDownloads "Biruscan-Setup.msi") -Force
    Copy-Item $updateZip (Join-Path $webDownloads "Biruscan-update.zip") -Force
    Copy-Item $updateZipNet48 (Join-Path $webDownloads "Biruscan-update-net48.zip") -Force
    Write-Host "`n[SUCCESS] Copied installer to $webDownloads" -ForegroundColor Green
}
