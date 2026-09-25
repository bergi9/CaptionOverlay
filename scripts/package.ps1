# Builds the portable release: self-contained folder publish → CaptionOverlay-vX.Y.Z-win-x64.zip + SHA256SUMS.txt
# Usage: powershell -NoProfile -File scripts/package.ps1 -Version 0.1.0
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$OutDir
)
$ErrorActionPreference = 'Stop'
# $PSScriptRoot is not available in parameter defaults on Windows PowerShell 5.1.
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot '..\artifacts' }
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$Version = $Version.TrimStart('v')
$name = "CaptionOverlay-v$Version-win-x64"
$staging = Join-Path $OutDir $name
$zip = Join-Path $OutDir "$name.zip"

if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
if (Test-Path $zip) { Remove-Item -Force $zip }
New-Item -ItemType Directory -Force $OutDir | Out-Null

# Folder deploy (not single-file): Whisper.net loads its native runtimes from runtimes\ next to the exe,
# which fails from a single-file bundle. See docs/decisions.md (ADR-003).
dotnet publish (Join-Path $root 'src\CaptionOverlay.App') -c Release -r win-x64 --self-contained true `
    -p:DebugType=none -p:Version=$Version -o $staging
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Copy-Item (Join-Path $root 'packaging\README.txt') $staging
Copy-Item (Join-Path $root 'packaging\THIRD_PARTY_NOTICES.txt') $staging
if (Test-Path (Join-Path $root 'LICENSE')) { Copy-Item (Join-Path $root 'LICENSE') $staging }
else { Write-Warning 'No LICENSE file in the repository root; the zip will not contain one.' }

Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $name.zip" | Set-Content -Encoding ascii (Join-Path $OutDir 'SHA256SUMS.txt')

$sizeMb = [Math]::Round((Get-ChildItem $staging -Recurse | Measure-Object Length -Sum).Sum / 1MB, 1)
$zipMb = [Math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Package: $zip ($zipMb MB zipped, $sizeMb MB extracted)"
Write-Host "SHA-256: $hash"
