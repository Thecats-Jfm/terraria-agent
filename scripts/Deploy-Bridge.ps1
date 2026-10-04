[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Workspace-Safety.psm1') -Force
$taskLayout = Get-WorkspaceLayout
Assert-NoGameRunning
$taskStatus = Get-Content -LiteralPath (Join-Path $taskLayout.Runtime 'preparation-status.json') -Raw | ConvertFrom-Json
if (-not $taskStatus.BackupStable -or -not $taskStatus.GameCopyVerified -or -not $taskStatus.ProcessVisibilityVerified) { throw 'Verified preparation is required.' }
$taskGameExe = Join-Path $taskLayout.Game 'Terraria.exe'
if ((Get-FileHash -LiteralPath $taskGameExe -Algorithm SHA256).Hash -ne $taskStatus.IsolatedGameExeSHA256) { throw 'The isolated game executable has changed.' }
$taskBuild = Join-Path $taskLayout.Build 'AgentHost'
$taskNames = @('TerrariaAgent.Host.exe', 'TerrariaAgent.Host.exe.config', 'TerrariaAgent.Bridge.dll', '0Harmony.dll')
$taskManifestPath = Join-Path $taskLayout.Runtime 'bridge-deployment.json'
$taskOld = $null
if (Test-Path -LiteralPath $taskManifestPath) { $taskOld = Get-Content -LiteralPath $taskManifestPath -Raw | ConvertFrom-Json }
$taskFiles = @()
foreach ($taskName in $taskNames) {
    $taskSource = Join-Path $taskBuild $taskName
    $taskTarget = Join-Path $taskLayout.Game $taskName
    Assert-WithinRoot -Path $taskSource -Root $taskLayout.Runtime
    Assert-WithinRoot -Path $taskTarget -Root $taskLayout.Runtime
    Assert-NoReparsePath -Path $taskSource
    Assert-NoReparsePath -Path $taskTarget
    if (-not (Test-Path -LiteralPath $taskSource -PathType Leaf)) { throw "Build output is missing: $taskName" }
    if (Test-Path -LiteralPath $taskTarget) {
        $taskRecorded = @(if ($null -ne $taskOld) { $taskOld.Files | Where-Object Name -EQ $taskName })
        if ($taskRecorded.Count -ne 1 -or (Get-FileHash -LiteralPath $taskTarget -Algorithm SHA256).Hash -ne $taskRecorded[0].SHA256) {
            throw "Refusing to replace an unrecognized or changed file: $taskTarget"
        }
    }
    $taskFiles += [pscustomobject]@{ Name = $taskName; SHA256 = (Get-FileHash -LiteralPath $taskSource -Algorithm SHA256).Hash }
}
foreach ($taskFile in $taskFiles) {
    Copy-Item -LiteralPath (Join-Path $taskBuild $taskFile.Name) -Destination (Join-Path $taskLayout.Game $taskFile.Name) -Force
    if ((Get-FileHash -LiteralPath (Join-Path $taskLayout.Game $taskFile.Name) -Algorithm SHA256).Hash -ne $taskFile.SHA256) { throw 'Deployed bytes failed verification.' }
}
Write-WorkspaceJson -Value ([ordered]@{ DeployedUtc = [DateTime]::UtcNow.ToString('o'); GameDirectory = $taskLayout.Game;
    GameExeSHA256 = $taskStatus.IsolatedGameExeSHA256; Files = $taskFiles; GameStarted = $false }) -Path $taskManifestPath -AllowedRoot $taskLayout.Runtime
Write-Output 'Own Host, bridge and pinned Harmony copied to isolated game directory. Original installation is untouched; game not started.'
