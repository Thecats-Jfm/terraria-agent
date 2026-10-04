[CmdletBinding()]
param(
    [ValidateSet('stage-a','stage-b','disconnect-test','expiry-test','manual-test','emergency-test')][string]$Mode='stage-a',
    [switch]$DesktopAvailable
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $DesktopAvailable) { throw 'This opens the game; use -DesktopAvailable only when the desktop is available for testing.' }
Import-Module (Join-Path $PSScriptRoot 'Workspace-Safety.psm1') -Force
$taskLayout = Get-WorkspaceLayout
$taskMarker = Join-Path $taskLayout.Runtime 'current-run.txt'
$taskPreviousRun = if (Test-Path -LiteralPath $taskMarker) { ([IO.File]::ReadAllText($taskMarker)).Trim() } else { '' }
& (Join-Path $PSScriptRoot 'Start-Game.ps1') -DesktopAvailable -AllowInitialControllerStart -EnableStageB:($Mode -eq 'stage-b')
# Never connect to a stale run while the newly started Host initializes.
$taskStartup = [Diagnostics.Stopwatch]::StartNew()
$taskReady = $false
while ($taskStartup.ElapsedMilliseconds -lt 5000 -and -not $taskReady) {
    if (Test-Path -LiteralPath $taskMarker) {
        $taskRun = ([IO.File]::ReadAllText($taskMarker)).Trim()
        if ($taskRun -and $taskRun -ne $taskPreviousRun) {
            Assert-WithinRoot -Path $taskRun -Root (Join-Path $taskLayout.Runtime 'logs')
            Assert-NoReparsePath -Path $taskRun
            $taskRunId = Split-Path -Leaf $taskRun
            if ($taskRunId -notmatch '^\d{8}T\d{6}Z-[a-f0-9]{8}$') { throw 'Invalid new run identity.' }
            $taskReady = Test-Path -LiteralPath (Join-Path $taskLayout.Runtime "ipc/$taskRunId.local.json") -PathType Leaf
        }
    }
    if (-not $taskReady) { Start-Sleep -Milliseconds 100 }
}
if (-not $taskReady) { throw 'New game connection is unavailable. No controller was started; inspect startup logs.' }
# The operator explicitly starts this one attempt. The bridge consumes the
# initial permission; this script never reconnects, retries or sends OS keys.
& (Join-Path $PSScriptRoot 'Run-Controller.ps1') -Mode $Mode -Arm -InitialStart -PermissionWaitSeconds 60
exit $LASTEXITCODE
