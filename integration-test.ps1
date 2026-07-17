<#
.SYNOPSIS
Runs encrypted publisher/subscriber streams through a real relay server.
#>

[CmdletBinding()]
param(
	[string] $ServerExe = 'C:\NVDARemoteAudioServer\NVDARemoteAudioServer.exe',
	[int] $Port = 16838
)

# The redirected async process APIs used below are reliable on modern .NET but
# can deadlock under Windows PowerShell 5.1's .NET Framework Process wrapper.
# Relaunch transparently in PowerShell 7 when this script is invoked by 5.1.
if ($PSVersionTable.PSVersion.Major -lt 7) {
	$pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
	if ($null -eq $pwsh) {
		throw 'integration-test.ps1 requires PowerShell 7 (pwsh)'
	}
	& $pwsh.Source -NoProfile -File $PSCommandPath -ServerExe $ServerExe -Port $Port
	exit $LASTEXITCODE
}

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$helperExe = Join-Path $repoRoot 'helper\bin\Release\net9.0-windows\NVDARemoteAudioHelper.exe'
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("remote-audio-integration-{0}" -f [guid]::NewGuid())

function Start-RedirectedProcess {
	param(
		[Parameter(Mandatory = $true)] [string] $FilePath,
		[Parameter(Mandatory = $true)] [string[]] $Arguments,
		[hashtable] $Environment = @{}
	)
	$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
	$startInfo.FileName = $FilePath
	$startInfo.UseShellExecute = $false
	$startInfo.CreateNoWindow = $true
	$startInfo.RedirectStandardInput = $true
	$startInfo.RedirectStandardOutput = $true
	$startInfo.RedirectStandardError = $true
	# Use the classic Arguments property so this test works in both Windows
	# PowerShell 5.1 and PowerShell 7. ArgumentList is unavailable on .NET
	# Framework and otherwise produces a null-valued expression there.
	$quotedArguments = foreach ($argument in $Arguments) {
		$text = [string] $argument
		if ($text -notmatch '[\s"]') {
			$text
			continue
		}
		$escaped = $text -replace '(\\*)"', '$1$1\"'
		$escaped = $escaped -replace '(\\+)$', '$1$1'
		'"{0}"' -f $escaped
	}
	$startInfo.Arguments = $quotedArguments -join ' '
	foreach ($entry in $Environment.GetEnumerator()) {
		$startInfo.EnvironmentVariables[$entry.Key] = [string] $entry.Value
	}
	$process = [System.Diagnostics.Process]::new()
	$process.StartInfo = $startInfo
	if (-not $process.Start()) {
		throw "Failed to start $FilePath"
	}
	# Drain both pipes immediately. Waiting to call ReadToEnd until after exit can
	# deadlock once a verbose helper or relay fills an OS pipe buffer.
	$process | Add-Member -NotePropertyName CapturedOutputTask -NotePropertyValue $process.StandardOutput.ReadToEndAsync()
	$process | Add-Member -NotePropertyName CapturedErrorTask -NotePropertyValue $process.StandardError.ReadToEndAsync()
	return $process
}

function Get-CapturedProcessOutput {
	param([Parameter(Mandatory = $true)] [System.Diagnostics.Process] $Process)
	return $Process.CapturedOutputTask.GetAwaiter().GetResult() + $Process.CapturedErrorTask.GetAwaiter().GetResult()
}

function Stop-HelperProcess {
	param([Parameter(Mandatory = $true)] [System.Diagnostics.Process] $Process)
	if (-not $Process.HasExited) {
		# Any byte is the helper's graceful shutdown signal. Writing avoids the
		# occasional Windows PowerShell StreamWriter.Close hang seen with a pending
		# async stdin read in the child.
		try {
			$Process.StandardInput.Write('x')
			$Process.StandardInput.Flush()
		}
		catch {
			# The process may have exited between HasExited and the write.
		}
		if (-not $Process.WaitForExit(5000)) {
			$Process.Kill($true)
			if (-not $Process.WaitForExit(5000)) {
				throw "Helper process $($Process.Id) did not exit after termination"
			}
		}
	}
	$Process.StandardInput.Dispose()
}

function Invoke-StreamCase {
	param(
		[Parameter(Mandatory = $true)] [string] $Codec,
		[Parameter(Mandatory = $true)] [string] $Key,
		[int] $DurationSeconds = 3
	)
	$recordFolder = Join-Path $testRoot $Codec
	Write-Host "Starting encrypted $Codec relay stream..."
	New-Item -ItemType Directory -Force -Path $recordFolder | Out-Null
	$passwordVariable = 'NVDA_REMOTE_AUDIO_INTEGRATION_PASSWORD'
	$environment = @{ $passwordVariable = 'integration test password' }
	$common = @(
		'--host', '127.0.0.1',
		'--port', [string] $Port,
		'--key', $Key,
		'--password-env', $passwordVariable,
		'--codec', $Codec,
		'--opus-frame-ms', '5'
	)
	$subscriber = Start-RedirectedProcess -FilePath $helperExe -Arguments (@(
		'--role', 'subscriber'
	) + $common + @(
		'--prebuffer-ms', '15',
		'--output-latency-ms', '15',
		'--buffer-ms', '120',
		'--receive-volume', '0',
		'--receive-pan', '20',
		'--bass-db', '2',
		'--mid-db', '-1',
		'--treble-db', '1',
		'--record-folder', $recordFolder
	)) -Environment $environment
	Start-Sleep -Milliseconds 300
	$publisher = Start-RedirectedProcess -FilePath $helperExe -Arguments (@(
		'--role', 'publisher'
	) + $common + @(
		'--test-tone',
		'--bitrate', '128000'
	)) -Environment $environment

	try {
		Start-Sleep -Seconds $DurationSeconds
	}
	finally {
		Stop-HelperProcess -Process $publisher
		Stop-HelperProcess -Process $subscriber
	}
	$publisherOutput = Get-CapturedProcessOutput -Process $publisher
	$subscriberOutput = Get-CapturedProcessOutput -Process $subscriber
	if ($publisher.ExitCode -ne 0 -or $subscriber.ExitCode -ne 0) {
		throw "$Codec stream failed. Publisher: $publisherOutput Subscriber: $subscriberOutput"
	}
	if ($publisherOutput -notmatch '"event":"connected"' -or $subscriberOutput -notmatch '"event":"connected"') {
		throw "$Codec stream did not connect both clients"
	}
	if ($publisherOutput -match '"event":"error"' -or $subscriberOutput -match '"event":"error"') {
		throw "$Codec stream reported an error. Publisher: $publisherOutput Subscriber: $subscriberOutput"
	}
	$recording = Get-ChildItem -LiteralPath $recordFolder -Filter '*.wav' | Select-Object -First 1
	if ($null -eq $recording -or $recording.Length -le 44) {
		throw "$Codec stream did not produce a non-empty WAV recording"
	}
	Write-Host "$Codec encrypted relay stream passed ($($recording.Length) byte recording)."
}

function Invoke-WrongPasswordCase {
	Write-Host 'Starting wrong-password rejection case...'
	$passwordVariable = 'NVDA_REMOTE_AUDIO_INTEGRATION_PASSWORD'
	$common = @(
		'--host', '127.0.0.1',
		'--port', [string] $Port,
		'--key', 'integration-wrong-password',
		'--password-env', $passwordVariable,
		'--codec', 'opus',
		'--opus-frame-ms', '5'
	)
	$subscriber = Start-RedirectedProcess -FilePath $helperExe -Arguments (@(
		'--role', 'subscriber'
	) + $common + @(
		'--prebuffer-ms', '15',
		'--output-latency-ms', '15',
		'--buffer-ms', '120',
		'--receive-volume', '0'
	)) -Environment @{ $passwordVariable = 'wrong password' }
	Start-Sleep -Milliseconds 300
	$publisher = Start-RedirectedProcess -FilePath $helperExe -Arguments (@(
		'--role', 'publisher'
	) + $common + @(
		'--test-tone',
		'--bitrate', '128000'
	)) -Environment @{ $passwordVariable = 'correct password' }
	try {
		if (-not $subscriber.WaitForExit(5000)) {
			throw 'Wrong-password subscriber did not reject the stream'
		}
	}
	finally {
		Stop-HelperProcess -Process $publisher
		Stop-HelperProcess -Process $subscriber
	}
	$subscriberOutput = Get-CapturedProcessOutput -Process $subscriber
	if ($subscriber.ExitCode -eq 0 -or $subscriberOutput -notmatch 'Unable to authenticate remote audio') {
		throw "Wrong-password stream did not fail clearly: $subscriberOutput"
	}
	Write-Host 'Wrong-password rejection passed.'
}

function Start-TestRelay {
	param([Parameter(Mandatory = $true)] [string] $LogName)
	return Start-RedirectedProcess -FilePath $ServerExe -Arguments @(
		"--port=$Port",
		"--sport=$($Port + 1)",
		"--log=$(Join-Path $testRoot $LogName)"
	)
}

function Invoke-RelayRestartCase {
	param([Parameter(Mandatory = $true)] [ref] $ServerProcess)

	Write-Host 'Starting relay-restart recovery case...'
	$publisher = Start-RedirectedProcess -FilePath $helperExe -Arguments @(
		'--role', 'publisher',
		'--host', '127.0.0.1',
		'--port', [string] $Port,
		'--key', 'integration-relay-restart',
		'--codec', 'opus',
		'--opus-frame-ms', '5',
		'--test-tone',
		'--bitrate', '128000'
	)
	try {
		Start-Sleep -Seconds 2
		$oldServer = $ServerProcess.Value
		if (-not $oldServer.HasExited) {
			$oldServer.Kill($true)
			$oldServer.WaitForExit()
		}
		$oldServer.Dispose()
		# Keep the relay down long enough for at least one reconnect attempt to fail.
		# This verifies that recovery continues instead of exiting after one retry.
		Start-Sleep -Seconds 8
		$ServerProcess.Value = Start-TestRelay -LogName 'server-restarted.log'
		Start-Sleep -Seconds 6
		if ($ServerProcess.Value.HasExited) {
			throw "Restarted relay exited: $(Get-CapturedProcessOutput -Process $ServerProcess.Value)"
		}
	}
	finally {
		Stop-HelperProcess -Process $publisher
	}

	$output = Get-CapturedProcessOutput -Process $publisher
	$connectedCount = ([regex]::Matches($output, '"event":"connected"')).Count
	$reconnectingCount = ([regex]::Matches($output, '"reconnecting":true')).Count
	if ($publisher.ExitCode -ne 0 -or $connectedCount -lt 2 -or $reconnectingCount -lt 2) {
		throw "Publisher did not recover after relay restart: $output"
	}
	Write-Host 'Relay-restart recovery passed.'
}

if (-not (Test-Path -LiteralPath $ServerExe)) {
	throw "Relay server not found at $ServerExe"
}
if (-not (Test-Path -LiteralPath $helperExe)) {
	throw "Helper not found at $helperExe; run run-tests.ps1 first"
}

New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
$server = Start-TestRelay -LogName 'server.log'
try {
	Start-Sleep -Milliseconds 600
	if ($server.HasExited) {
		throw "Relay server exited during startup: $(Get-CapturedProcessOutput -Process $server)"
	}
	# The relay's control idle timeout is 15 seconds. Keeping one real stream alive
	# past that boundary proves TCP heartbeat traffic is actually reaching it.
	Invoke-StreamCase -Codec 'opus' -Key 'integration-opus' -DurationSeconds 18
	Invoke-StreamCase -Codec 'pcm' -Key 'integration-pcm'
	Invoke-WrongPasswordCase
	Invoke-RelayRestartCase -ServerProcess ([ref] $server)
	Write-Host 'All encrypted relay integration tests passed.'
}
finally {
	if (-not $server.HasExited) {
		$server.Kill($true)
		$server.WaitForExit()
	}
	$server.Dispose()
	if (Test-Path -LiteralPath $testRoot) {
		Remove-Item -LiteralPath $testRoot -Recurse -Force
	}
}
