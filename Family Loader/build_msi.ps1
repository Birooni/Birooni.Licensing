$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
Set-Location $Root

Write-Host "Building 2023 (Revit 2020-2024) and 2025 (Revit 2025-2027)..."
dotnet build "$Root\2023\FamilyLoader.csproj" -c Debug -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw "2023 build failed" }
dotnet build "$Root\2025\FamilyLoader.csproj" -c Debug -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw "2025 build failed" }

Write-Host "Installing WiX UI Extension..."
if (Test-Path ".wix") { Remove-Item ".wix" -Recurse -Force }
try { wix extension add WixToolset.UI.wixext/4.0.5 } catch {}

Write-Host "Setting up Source Directories..."
$SourceDir = "SourceDir"
if (Test-Path $SourceDir) { Remove-Item $SourceDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $SourceDir | Out-Null

$versions = @(
    @{ Name="2020"; Source="2023" },
    @{ Name="2021"; Source="2023" },
    @{ Name="2022"; Source="2023" },
    @{ Name="2023"; Source="2023" },
    @{ Name="2024"; Source="2023" },
    @{ Name="2025"; Source="2025" },
    @{ Name="2026"; Source="2025" },
    @{ Name="2027"; Source="2025" }
)

foreach ($v in $versions) {
    $verName = $v.Name
    $srcFolder = $v.Source
    
    $verDir = "$SourceDir\$verName"
    $appDir = "$verDir\Family Loader"
    New-Item -ItemType Directory -Force -Path $appDir | Out-Null
    
    Copy-Item "$srcFolder\FamilyLoader.addin" -Destination "$verDir\" -Force
    $binDir = "$srcFolder\bin\x64\Debug"
    if (-not (Test-Path $binDir)) { $binDir = "$srcFolder\bin\Debug" }
    Get-ChildItem $binDir -File | Copy-Item -Destination $appDir -Force
}

Write-Host "Generating Components.wxs..."

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

$global:compCounter = 1
$features = ""

function Get-WixDirTree {
    param($Path, $Indent, [ref]$compList, $VerName)
    $out = ""
    $items = Get-ChildItem -Path $Path

    foreach ($file in $items | Where-Object { !($_.PSIsContainer) }) {
        $compId = "Comp_$global:compCounter"
        $global:compCounter++
        $compList.Value += $compId
        $guid = [guid]::NewGuid().ToString()
        $out += "$Indent<Component Id=`"$compId`" Guid=`"$guid`" Condition=`"REVIT_$VerName = &quot;1&quot;`">`n"
        $out += "$Indent  <File Source=`"$($file.FullName)`" KeyPath=`"yes`" />`n"
        $out += "$Indent</Component>`n"
    }

    foreach ($dir in $items | Where-Object { $_.PSIsContainer }) {
        $out += "$Indent<Directory Name=`"$($dir.Name)`">`n"
        $out += Get-WixDirTree -Path $dir.FullName -Indent "$Indent  " -compList $compList -VerName $VerName
        $out += "$Indent</Directory>`n"
    }
    return $out
}

$compGroups = ""

foreach ($v in $versions) {
    $verName = $v.Name
    $xml += "            <Directory Id=`"DIR_REVIT_$verName`" Name=`"$verName`">`n"
    
    $verList = @()
    $xml += Get-WixDirTree -Path "$SourceDir\$verName" -Indent "              " -compList ([ref]$verList) -VerName $verName
    
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

Set-Content -Path "Components.wxs" -Value $xml

Write-Host "Running WiX build..."
wix build Installer.wxs Components.wxs CustomUI.wxs -ext WixToolset.UI.wixext -out Output\FamilyLoader_Setup.msi

Write-Host "Done! MSI created in Output\FamilyLoader_Setup.msi"
