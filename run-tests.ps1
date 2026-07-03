<#
.SYNOPSIS
Runs local validation for the helper and NVDA add-on packaging inputs.
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$helperProj = Join-Path $repoRoot 'helper\NVDARemoteAudioHelper.csproj'
$addonDir = Join-Path $repoRoot 'addon'
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("remoteAudioClient-tests-{0}" -f [guid]::NewGuid())
$publishDir = Join-Path $testRoot 'publish'

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

function Assert-Contains {
	param(
		[Parameter(Mandatory = $true)]
		[string] $Text,
		[Parameter(Mandatory = $true)]
		[string] $Needle
	)
	if (-not $Text.Contains($Needle)) {
		throw "Expected text to contain '$Needle'"
	}
}

try {
	New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

	Write-Host 'Building helper...'
	Invoke-CheckedCommand -FilePath 'dotnet' -Arguments @('build', $helperProj, '-c', 'Release')

	Write-Host 'Publishing helper smoke-test build...'
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
	if (-not (Test-Path -LiteralPath $helperExe)) {
		throw "Helper EXE not found at $helperExe"
	}
	$helpText = (& $helperExe --help) -join "`n"
	if ($LASTEXITCODE -ne 0) {
		throw "Helper --help failed with exit code $LASTEXITCODE"
	}
	foreach ($expected in '--role', '--host', '--key', '--opus-frame-ms', '--disable-fec', '--prebuffer-ms') {
		Assert-Contains $helpText $expected
	}

	$python = Get-Command python -ErrorAction SilentlyContinue
	if ($null -eq $python) {
		throw 'python is required for add-on syntax validation'
	}
	Write-Host 'Checking add-on source tree for generated files...'
	$generated = Get-ChildItem -LiteralPath $addonDir -Recurse -Force -File -ErrorAction SilentlyContinue |
		Where-Object {
			$_.Name -match '\.pyc$|\.pdb$|\.log$|\.tmp$|\.tmp\.' -or
			$_.FullName -match '\\__pycache__\\' -or
			$_.Name -in @('remoteAudioClient.json')
		}
	if ($generated) {
		throw "Generated files found under addon/: $($generated.FullName -join ', ')"
	}
	Write-Host 'Compiling Python add-on modules...'
	Invoke-CheckedCommand -FilePath $python.Source -Arguments @(
		'-m',
		'py_compile',
		(Join-Path $addonDir 'globalPlugins\remoteAudioClient\__init__.py'),
		(Join-Path $addonDir 'globalPlugins\remoteAudioClient\server_installer.py')
	)
	Get-ChildItem -LiteralPath $addonDir -Recurse -Directory -Filter '__pycache__' -Force -ErrorAction SilentlyContinue |
		Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

	Write-Host 'Validating manifest and documentation...'
	$manifestPath = Join-Path $addonDir 'manifest.ini'
	$manifest = Get-Content -LiteralPath $manifestPath -Raw
	$docFileName = ([regex]::Match($manifest, '(?im)^\s*docFileName\s*=\s*([^\r\n]+)\s*$')).Groups[1].Value.Trim()
	if (-not $docFileName) {
		throw 'manifest.ini is missing docFileName'
	}
	if (-not (Test-Path -LiteralPath (Join-Path $addonDir $docFileName))) {
		throw "manifest.ini docFileName '$docFileName' does not exist"
	}
	$version = ([regex]::Match($manifest, '(?im)^\s*version\s*=\s*([^\r\n]+)\s*$')).Groups[1].Value.Trim()
	if ($version -notmatch '^\d+\.\d+\.\d+$') {
		throw "manifest.ini version '$version' must use major.minor.patch"
	}

	Write-Host 'All checks passed.'
}
finally {
	Get-ChildItem -LiteralPath $addonDir -Recurse -Directory -Filter '__pycache__' -Force -ErrorAction SilentlyContinue |
		Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
	if (Test-Path -LiteralPath $testRoot) {
		Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
	}
}
