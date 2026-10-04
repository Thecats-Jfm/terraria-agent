[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Workspace-Safety.psm1') -Force
$taskLayout = Get-WorkspaceLayout
$taskStop = Join-Path $taskLayout.Runtime 'STOP'
$taskStopLock = Join-Path $taskLayout.Runtime 'STOP.lock'
Assert-WithinRoot -Path $taskStop -Root $taskLayout.Runtime
Assert-NoReparsePath -Path $taskStop
Assert-WithinRoot -Path $taskStopLock -Root $taskLayout.Runtime
Assert-NoReparsePath -Path $taskStopLock
$taskLockTimer = [Diagnostics.Stopwatch]::StartNew()
$taskStopGuard = $null
try {
    while ($null -eq $taskStopGuard -and $taskLockTimer.ElapsedMilliseconds -lt 2000) {
        try {
            $taskStopGuard = [IO.File]::Open($taskStopLock, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        }
        catch [IO.IOException] {
            if ($taskLockTimer.ElapsedMilliseconds -ge 2000) { break }
            Start-Sleep -Milliseconds 25
        }
    }
    if ($null -eq $taskStopGuard) { throw 'Could not acquire the local stop lock within 2 seconds. Stop was not delivered; use the in-game emergency hotkey or close the own Host.' }
    [IO.File]::WriteAllText($taskStop, [DateTime]::UtcNow.ToString('o'))
}
finally { if ($null -ne $taskStopGuard) { $taskStopGuard.Dispose() } }
Write-Output 'Agent stop latched. The game stays available for manual play. Explicit in-game arm permission is required again.'
