[CmdletBinding()]
param(
    [ValidateSet('observe', 'stage-a', 'stage-b', 'disconnect-test', 'expiry-test', 'manual-test', 'emergency-test')][string]$Mode = 'observe',
    [switch]$Arm,
    [switch]$InitialStart,
    [ValidateRange(1, 900)][int]$Seconds = 20,
    [ValidateRange(1, 60)][int]$PermissionWaitSeconds = 15
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($InitialStart -and $Mode -eq 'observe') { throw '-InitialStart requires an action mode and -Arm; observe never requests control.' }
if ($Mode -ne 'observe' -and -not $Arm) { throw 'Action modes require an explicit -Arm.' }
Import-Module (Join-Path $PSScriptRoot 'Workspace-Safety.psm1') -Force
$taskLayout = Get-WorkspaceLayout
$taskMarker = Join-Path $taskLayout.Runtime 'current-run.txt'
$taskRun = [IO.Path]::GetFullPath(([IO.File]::ReadAllText($taskMarker)).Trim())
Assert-WithinRoot -Path $taskRun -Root (Join-Path $taskLayout.Runtime 'logs')
Assert-NoReparsePath -Path $taskRun
$taskRunId = Split-Path -Leaf $taskRun
if ($taskRunId -notmatch '^\d{8}T\d{6}Z-[a-f0-9]{8}$') { throw 'Current run identity is invalid.' }
$taskConnection = Join-Path $taskLayout.Runtime "ipc/$taskRunId.local.json"
Assert-WithinRoot -Path $taskConnection -Root $taskLayout.Runtime
Assert-NoReparsePath -Path $taskConnection
if (-not (Test-Path -LiteralPath $taskConnection -PathType Leaf)) { throw 'The current game connection is unavailable; wait for startup or start a new game run.' }
$taskDll = Join-Path $taskLayout.Build 'Controller/TerrariaAgent.Controller.dll'
if (-not (Test-Path -LiteralPath $taskDll -PathType Leaf)) { throw 'Controller is not built.' }
$taskArguments = @($taskDll, '--connection', $taskConnection, '--mode', $Mode, '--seconds', [string]$Seconds,
    '--permission-wait-ms', [string]($PermissionWaitSeconds * 1000))
if ($Arm) { $taskArguments += '--arm' }
if ($InitialStart) { $taskArguments += '--initial-start' }
& dotnet @taskArguments
exit $LASTEXITCODE
