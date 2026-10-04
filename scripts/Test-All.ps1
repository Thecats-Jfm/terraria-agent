[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Workspace-Safety.psm1') -Force
$taskLayout = Get-WorkspaceLayout
$taskConfig = Join-Path $taskLayout.Runtime 'nuget.offline.config'
Assert-NoReparsePath -Path $taskConfig
if (-not (Test-Path -LiteralPath $taskConfig -PathType Leaf)) {
    throw 'Run Build.ps1 once to prepare the reviewed local dependency configuration.'
}
$taskDotnet = Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1
$taskStamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$taskRoot = Join-Path $taskLayout.Runtime "checks/$taskStamp"
New-SafeDirectory -Path $taskRoot -Root $taskLayout.Runtime
$taskLog = Join-Path $taskRoot 'checks.log'
$taskReport = Join-Path $taskRoot 'checks.json'
$taskSummary = [ordered]@{
    StartedUtc = [DateTime]::UtcNow.ToString('o')
    Scope = 'Offline checks only; no game, desktop, save, or Boss acceptance'
    GameStarted = $false
    Status = 'running'
    Results = @()
}

function Invoke-CheckCommand {
    param([string[]]$Arguments)
    & $taskDotnet.Source @Arguments 2>&1 | Tee-Object -FilePath $taskLog -Append | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Check command failed ($LASTEXITCODE). See $taskLog" }
}

try {
    foreach ($taskName in @('ProtocolChecks', 'VisibilityChecks', 'AutoPauseChecks',
        'SkillChecks', 'CProtocolChecks', 'CSkillChecks', 'WalkingSlopeChecks')) {
        $taskProject = Join-Path $taskLayout.Project "tests/$taskName/$taskName.csproj"
        Assert-WithinRoot -Path $taskProject -Root $taskLayout.Project
        Assert-NoReparsePath -Path $taskProject
        $taskOutput = Join-Path $taskRoot $taskName
        New-SafeDirectory -Path $taskOutput -Root $taskRoot
        Write-Host "Offline checks: $taskName"
        Invoke-CheckCommand -Arguments @('restore', $taskProject, '--configfile', $taskConfig,
            '-p:NuGetAudit=false')
        Invoke-CheckCommand -Arguments @('build', $taskProject, '--no-restore', '-c', 'Release',
            '-o', $taskOutput, '-p:NuGetAudit=false')
        $taskCheckArguments = @((Join-Path $taskOutput "$taskName.dll"))
        if ($taskName -eq 'CProtocolChecks') {
            $taskEvidence = Join-Path $taskOutput 'evidence'
            New-SafeDirectory -Path $taskEvidence -Root $taskRoot
            $taskCheckArguments += @('--evidence-directory', $taskEvidence)
        }
        Invoke-CheckCommand -Arguments $taskCheckArguments
        $taskSummary.Results += [ordered]@{ Project = $taskName; Status = 'passed'; ExitCode = 0 }
    }
    $taskSummary.Status = 'passed'
} catch {
    $taskSummary.Status = 'failed'
    $taskSummary.Error = $_.Exception.Message
    throw
} finally {
    $taskSummary.FinishedUtc = [DateTime]::UtcNow.ToString('o')
    $taskSummary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $taskReport -Encoding UTF8
    Write-Host "Offline check report: $taskReport"
}
