<#
.SYNOPSIS
  Publish the tray app (self-contained single-file, win-x64) next to the
  native core DLL and the default config, then zip it. Used by CI and
  runnable locally.
.EXAMPLE
  .\package.ps1 -Version 0.1.0 -CoreDll ..\core\build\Release\PeakModeCore.dll
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $CoreDll,
    [string] $OutDir = "$PSScriptRoot\..\dist"
)
$ErrorActionPreference = 'Stop'
$root    = (Resolve-Path "$PSScriptRoot\..").Path
$CoreDll = (Resolve-Path $CoreDll).Path
$OutDir  = [IO.Path]::GetFullPath($OutDir)
$name    = "PeakMode-$Version-win-x64"
$stage   = Join-Path $OutDir $name
$zip     = Join-Path $OutDir "$name.zip"

if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
if (Test-Path $zip)   { Remove-Item -Force $zip }
New-Item -ItemType Directory -Force $stage | Out-Null

dotnet publish "$root\ui\PeakModeTray" -c Release -o $stage "-p:CoreDllPath=$CoreDll" "-p:Version=$Version"
if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)" }

Copy-Item "$root\LICENSE", "$root\README.md" $stage
Copy-Item "$root\scripts\RECOVERY.md" $stage
Copy-Item "$root\core\third_party\nvapi\License.txt" (Join-Path $stage 'THIRD-PARTY-NVAPI-LICENSE.txt')
Copy-Item "$root\core\third_party\nlohmann\LICENSE.MIT" (Join-Path $stage 'THIRD-PARTY-NLOHMANN-JSON-LICENSE.txt')
# Fonts bundled in the exe (SIL OFL 1.1)
$fontLicenses = 'ChakraPetch', 'Inter', 'JetBrainsMono'
foreach ($f in $fontLicenses) {
    Copy-Item "$root\ui\PeakModeTray\Assets\Fonts\$f-OFL.txt" (Join-Path $stage "THIRD-PARTY-FONT-$($f.ToUpper())-OFL.txt")
}

# The release is only useful if all three runtime pieces are present
$required = @('PeakModeTray.exe', 'PeakModeCore.dll', 'config\peakmode.json') +
            ($fontLicenses | ForEach-Object { "THIRD-PARTY-FONT-$($_.ToUpper())-OFL.txt" })
foreach ($f in $required) {
    if (-not (Test-Path (Join-Path $stage $f))) { throw "package is missing $f" }
}
$extra = Get-ChildItem $stage -Recurse -File | Where-Object { $_.Extension -in '.pdb', '.json' -and $_.Name -ne 'peakmode.json' }
if ($extra) { throw "unexpected files in package: $($extra.Name -join ', ')" }

Compress-Archive -Path "$stage\*" -DestinationPath $zip
Get-ChildItem $stage -Recurse -File | ForEach-Object {
    "{0,12:N0}  {1}" -f $_.Length, $_.FullName.Substring($stage.Length + 1)
}
"zip: $zip ($('{0:N1}' -f ((Get-Item $zip).Length / 1MB)) MB)"
