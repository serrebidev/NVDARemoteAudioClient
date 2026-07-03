<#
.SYNOPSIS
Builds NVDARemoteAudioHelper.exe and packages the NVDA add-on (.nvda-addon).

.DESCRIPTION
Publishes the .NET helper as a self-contained single-file Windows x64 EXE, stages
only the runtime add-on files into a clean temporary directory, validates the
archive shape, and writes remoteAudioClient-<version>.nvda-addon under dist/.

Requires:
  - .NET 9 SDK (https://dotnet.microsoft.com/download)
  - Python for syntax validation
  - Windows 10 build 20348+ at runtime (process-loopback exclusion API)

.EXAMPLE
.\build.ps1
#>

[CmdletBinding()]
param(
	[switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$addonDir = Join-Path $repoRoot 'addon'
$helperProj = Join-Path $repoRoot 'helper\NVDARemoteAudioHelper.csproj'
$publishDir = Join-Path $repoRoot 'publish\win-x64'
$distDir = Join-Path $repoRoot 'dist'

function Invoke-CheckedCommand {
	param(
		[Parameter(Mandatory = $true)]
		[string] $FilePath,
		[string[]] $Arguments = @()
	)
	& $FilePath @Arguments | Out-Host
	if ($LASTEXITCODE -ne 0) {
		throw "$FilePath failed with exit code $LASTEXITCODE"
	}
}

function Test-AddonPackage {
	param(
		[Parameter(Mandatory = $true)]
		[string] $PackagePath
	)
	Add-Type -AssemblyName System.IO.Compression.FileSystem
	$zip = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
	try {
		$entries = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
		foreach ($required in @('manifest.ini', 'readme.html', 'globalPlugins/remoteAudioClient/__init__.py', 'globalPlugins/remoteAudioClient/server_installer.py', 'bin/NVDARemoteAudioHelper.exe')) {
			if ($entries -notcontains $required) {
				throw "Package is missing required entry '$required'"
			}
		}
		$forbidden = $entries | Where-Object {
			$_ -match '(^|/)addon/' -or
			$_ -match '(^|/)README\.md$' -or
			$_ -match '(^|/)__pycache__/' -or
			$_ -match '\.pyc$|\.pdb$|\.log$|\.tmp$|\.tmp\.|\.zip$|\.nvda-addon$' -or
			$_ -match 'remoteAudioClient\.json$'
		}
		if ($forbidden) {
			throw "Package contains forbidden entries: $($forbidden -join ', ')"
		}
	}
	finally {
		$zip.Dispose()
	}
}

if (-not $SkipTests) {
	& (Join-Path $repoRoot 'run-tests.ps1')
	if ($LASTEXITCODE -ne 0) {
		throw "run-tests.ps1 failed with exit code $LASTEXITCODE"
	}
}

$manifest = Get-Content -LiteralPath (Join-Path $addonDir 'manifest.ini') -Raw
$version = ([regex]::Match($manifest, '(?im)^\s*version\s*=\s*([^\r\n]+)\s*$')).Groups[1].Value.Trim()
if (-not $version) { throw 'Could not read version from addon/manifest.ini' }
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Version '$version' must use major.minor.patch" }

Write-Host 'Publishing helper...'
Invoke-CheckedCommand -FilePath 'dotnet' -Arguments @(
	'publish',
	$helperProj,
	'-c',
	'Release',
	'-r',
	'win-x64',
	'--self-contained',
	'true',
	'-o',
	$publishDir,
	'/p:PublishSingleFile=true',
	'/p:IncludeNativeLibrariesForSelfExtract=true',
	'/p:EnableCompressionInSingleFile=true'
)

$helperExe = Join-Path $publishDir 'NVDARemoteAudioHelper.exe'
if (-not (Test-Path -LiteralPath $helperExe)) { throw "Helper EXE not found at $helperExe" }

New-Item -ItemType Directory -Force -Path $distDir | Out-Null
$packagePath = Join-Path $distDir ("remoteAudioClient-{0}.nvda-addon" -f $version)
$tempZip = Join-Path $distDir ("remoteAudioClient-{0}.zip" -f $version)
Remove-Item -LiteralPath $packagePath, $tempZip -ErrorAction SilentlyContinue

$stageRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("remoteAudioClient-package-{0}" -f [guid]::NewGuid())
$stageDir = Join-Path $stageRoot 'addon'
try {
	New-Item -ItemType Directory -Force -Path (Join-Path $stageDir 'bin') | Out-Null
	Copy-Item -LiteralPath (Join-Path $addonDir 'manifest.ini') -Destination (Join-Path $stageDir 'manifest.ini') -Force
	Copy-Item -LiteralPath (Join-Path $addonDir 'readme.html') -Destination (Join-Path $stageDir 'readme.html') -Force
	Copy-Item -LiteralPath (Join-Path $addonDir 'globalPlugins') -Destination (Join-Path $stageDir 'globalPlugins') -Recurse -Force
	Copy-Item -LiteralPath $helperExe -Destination (Join-Path $stageDir 'bin\NVDARemoteAudioHelper.exe') -Force

	$generated = Get-ChildItem -LiteralPath $stageDir -Recurse -Force -File -ErrorAction SilentlyContinue |
		Where-Object {
			$_.Name -match '\.pyc$|\.pdb$|\.log$|\.tmp$|\.tmp\.|\.zip$|\.nvda-addon$' -or
			$_.FullName -match '\\__pycache__\\' -or
			$_.Name -in @('remoteAudioClient.json', 'README.md')
		}
	if ($generated) {
		throw "Refusing to package generated/source-only files: $($generated.FullName -join ', ')"
	}

	Write-Host 'Packaging add-on...'
	Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $tempZip -CompressionLevel Optimal -Force
	Move-Item -LiteralPath $tempZip -Destination $packagePath -Force
	Test-AddonPackage -PackagePath $packagePath
}
finally {
	if (Test-Path -LiteralPath $stageRoot) {
		Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
	}
}

Write-Host ("Built {0}" -f $packagePath)
