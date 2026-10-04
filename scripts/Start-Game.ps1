[CmdletBinding()]
param([switch]$DesktopAvailable, [switch]$AllowInitialControllerStart, [switch]$EnableStageB)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Workspace-Safety.psm1') -Force
$taskLayout = Get-WorkspaceLayout
if (-not $DesktopAvailable) { throw 'This opens the game. Run with -DesktopAvailable only after the desktop is free for game testing.' }
Assert-NoGameRunning
$taskStop = Join-Path $taskLayout.Runtime 'STOP'
Assert-NoReparsePath -Path $taskStop
if ($AllowInitialControllerStart -and (Test-Path -LiteralPath $taskStop)) {
    throw 'An existing emergency STOP blocks initial controller start. This script does not clear it.'
}
$taskStatus = Get-Content -LiteralPath (Join-Path $taskLayout.Runtime 'preparation-status.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($taskStatus.Status -ne 'prepared_not_launched' -or -not $taskStatus.BackupStable -or -not $taskStatus.GameCopyVerified -or -not $taskStatus.ProcessVisibilityVerified) {
    throw 'A stable, verified preparation in a desktop-visible context is required.'
}
$taskBackupVerification = @(& (Join-Path $PSScriptRoot 'Verify-Backup.ps1'))
if ($taskBackupVerification.Count -ne 1 -or $taskBackupVerification[0].Status -ne 'verified_read_only') {
    throw 'Original saves and preserved backup must pass the read-only verification immediately before launch.'
}
Write-Output "Save backup bytes verified: $($taskBackupVerification[0].FileCount) files; timestamp-only differences=$($taskBackupVerification[0].MetadataOnlyTimestampChanges)."
& (Join-Path $PSScriptRoot 'Deploy-Bridge.ps1')
# XNA TitleLocation uses the entry assembly directory, not the working directory.
# Keep the fixed Host next to the copied Terraria.exe and Content/.
$taskHost = Join-Path $taskLayout.Game 'TerrariaAgent.Host.exe'
Assert-WithinRoot -Path $taskHost -Root $taskLayout.Runtime
Assert-NoReparsePath -Path $taskHost
if (-not (Test-Path -LiteralPath $taskHost -PathType Leaf)) { throw 'Build the reviewed sources before starting the game.' }
$taskStamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$taskStdout = Join-Path $taskLayout.Runtime "logs/launch-$taskStamp.stdout.log"
$taskStderr = Join-Path $taskLayout.Runtime "logs/launch-$taskStamp.stderr.log"
# The console helper is hidden. The game creates its own interactive window.
# Initial authorization is optional, explicit and consumed once by the bridge;
# this launcher never synthesizes keyboard/mouse input or arms a controller.
$taskHostArguments = @('--runtime-root', ('"' + $taskLayout.Runtime + '"'))
if ($AllowInitialControllerStart) { $taskHostArguments += '--allow-initial-controller-start' }
if ($EnableStageB) { $taskHostArguments += '--enable-stage-b' }
$taskProcess = Start-Process -FilePath $taskHost -ArgumentList $taskHostArguments `
    -WorkingDirectory $taskLayout.Game -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput $taskStdout -RedirectStandardError $taskStderr
Write-Output "Game host PID: $($taskProcess.Id). Controls default to manual; rule controller is separate."
if ($AllowInitialControllerStart) { Write-Output 'One initial explicit controller start is authorized; stops and reconnections cannot renew it.' }
Write-Output "Startup logs: $taskStdout"
