<#
.SYNOPSIS
Builds NVDARemoteAudioHelper.exe and packages the NVDA add-on (.nvda-addon).

.DESCRIPTION
Publishes the .NET helper as a self-contained single-file Windows x64 EXE, copies
it into addon/bin/, and zips the addon/ directory into
remoteAudioClient-<version>.nvda-addon at the repo root.

Requires:
  - .NET 9 SDK (https://dotnet.microsoft.com/download)
  - Windows 10 build 20348+ at runtime (process-loopback exclusion API)

.EXAMPLE
.\build.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$addonDir = Join-Path $repoRoot 'addon'
$helperProj = Join-Path $repoRoot 'helper\NVDARemoteAudioHelper.csproj'
$publishDir = Join-Path $repoRoot 'publish\win-x64'
$addonBinDir = Join-Path $addonDir 'bin'

$manifest = Get-Content -LiteralPath (Join-Path $addonDir 'manifest.ini') -Raw
$version = ([regex]::Match($manifest, '(?im)^\s*version\s*=\s*([^\r\n]+)\s*$')).Groups[1].Value.Trim()
if (-not $version) { throw 'Could not read version from addon/manifest.ini' }

Write-Host "Publishing helper..."
dotnet publish $helperProj -c Release -r win-x64 --self-contained true -o $publishDir `
  /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$helperExe = Join-Path $publishDir 'NVDARemoteAudioHelper.exe'
if (-not (Test-Path -LiteralPath $helperExe)) { throw "Helper EXE not found at $helperExe" }

New-Item -ItemType Directory -Force -Path $addonBinDir | Out-Null
Copy-Item -LiteralPath $helperExe -Destination (Join-Path $addonBinDir 'NVDARemoteAudioHelper.exe') -Force

$packagePath = Join-Path $repoRoot ("remoteAudioClient-{0}.nvda-addon" -f $version)
$tempZip = Join-Path $repoRoot ("remoteAudioClient-{0}.zip" -f $version)
Remove-Item -LiteralPath $packagePath, $tempZip -ErrorAction SilentlyContinue

Write-Host "Packaging add-on..."
Compress-Archive -Path (Join-Path $addonDir '*') -DestinationPath $tempZip -CompressionLevel Optimal -Force
Move-Item -LiteralPath $tempZip -Destination $packagePath -Force

Write-Host ("Built {0}" -f $packagePath)
