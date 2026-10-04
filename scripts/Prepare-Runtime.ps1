[CmdletBinding()]
param(
    [string]$TerrariaInstallPath = 'E:\SteamLibrary\steamapps\common\Terraria',
    [string[]]$SteamRoot = @(),
    [ValidateRange(0, 10000)][int]$ConsistencyDelayMilliseconds = 1000
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Workspace-Safety.psm1') -Force
$layout = Get-WorkspaceLayout
Assert-NoGameRunning
if (-not [IO.Path]::IsPathRooted($TerrariaInstallPath)) { throw 'TerrariaInstallPath must be absolute.' }
$install = [IO.Path]::GetFullPath($TerrariaInstallPath).TrimEnd([char[]]@('\', '/'))
Assert-NoReparsePath -Path $install
if (-not (Test-Path -LiteralPath (Join-Path $install 'Terraria.exe') -PathType Leaf)) {
    throw "Terraria.exe was not found in the requested source install: $install"
}
if ($install.StartsWith($layout.Runtime + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The source installation must not be inside this runtime directory.'
}
foreach ($moddedPath in @('TerrariaModder', 'Mods', 'TerrariaInjector.exe')) {
    if (Test-Path -LiteralPath (Join-Path $install $moddedPath)) {
        throw "The source install already contains mod-loader files ($moddedPath); review it before copying."
    }
}
Assert-WithinRoot -Path $layout.Game -Root $layout.Runtime
Assert-NoReparsePath -Path $layout.Game
if ((Test-Path -LiteralPath $layout.Game) -and @(Get-ChildItem -LiteralPath $layout.Game -Force).Count -gt 0) {
    throw 'The isolated game directory already contains files. Nothing will be overwritten; inspect its prior status first.'
}

function Find-SteamRoots {
    $candidates = @($SteamRoot)
    foreach ($registryPath in @('HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\Wow6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam')) {
        $entry = Get-ItemProperty -LiteralPath $registryPath -ErrorAction SilentlyContinue
        if ($null -eq $entry) { continue }
        foreach ($property in @('SteamPath', 'InstallPath')) {
            $value = $entry.PSObject.Properties[$property]
            if ($null -ne $value -and -not [string]::IsNullOrWhiteSpace([string]$value.Value)) {
                $candidates += [string]$value.Value
            }
        }
    }
    $candidates += @((Join-Path ${env:ProgramFiles(x86)} 'Steam'), (Join-Path $env:ProgramFiles 'Steam'))
    $roots = @($candidates | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object {
        if (-not [IO.Path]::IsPathRooted($_)) { throw 'SteamRoot values must be absolute.' }
        $full = [IO.Path]::GetFullPath($_).TrimEnd([char[]]@('\', '/'))
        if ((Test-Path -LiteralPath (Join-Path $full 'steam.exe')) -or (Test-Path -LiteralPath (Join-Path $full 'userdata'))) {
            Assert-NoReparsePath -Path $full
            $full
        }
    } | Sort-Object -Unique)
    if ($roots.Count -eq 0) { throw 'Steam installation could not be located. Supply -SteamRoot explicitly before claiming a cloud backup.' }
    return $roots
}

$steamRoots = @(Find-SteamRoots)
$documents = [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)
if ([string]::IsNullOrWhiteSpace($documents)) { throw 'The Windows Documents folder could not be resolved.' }
$localRoot = Join-Path $documents 'My Games/Terraria'
Assert-NoReparsePath -Path $localRoot

function Get-SaveSnapshot {
    $locations = @(
        [pscustomobject]@{ Id = 'local-players'; Path = Join-Path $localRoot 'Players'; Kind = 'Directory' },
        [pscustomobject]@{ Id = 'local-worlds'; Path = Join-Path $localRoot 'Worlds'; Kind = 'Directory' }
    )
    $index = 0
    foreach ($root in $steamRoots) {
        $userdata = Join-Path $root 'userdata'
        Assert-NoReparsePath -Path $userdata
        if (Test-Path -LiteralPath $userdata) {
            foreach ($account in @(Get-ChildItem -LiteralPath $userdata -Directory -Force | Where-Object Name -Match '^\d+$' | Sort-Object Name)) {
                Assert-NoReparsePath -Path $account.FullName
                $app = Join-Path $account.FullName '105600'
                if (Test-Path -LiteralPath $app) {
                    $prefix = 'cloud-{0}-{1}' -f $index, $account.Name
                    $locations += [pscustomobject]@{ Id = "$prefix-remote"; Path = Join-Path $app 'remote'; Kind = 'Directory' }
                    $locations += [pscustomobject]@{ Id = "$prefix-cache"; Path = Join-Path $app 'remotecache.vdf'; Kind = 'File' }
                }
            }
        }
        $index++
    }
    @($locations | Sort-Object Id | ForEach-Object {
        [pscustomobject]@{ Id = $_.Id; Data = Get-TreeManifest -Path $_.Path -Kind $_.Kind }
    })
}

$attempt = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$backup = Join-Path $layout.Runtime "backups/$attempt"
$statusPath = Join-Path $layout.Runtime 'preparation-status.json'
$status = [ordered]@{
    SchemaVersion = 1; StartedUtc = [DateTime]::UtcNow.ToString('o'); Status = 'preparing'
    BackupDirectory = $backup; SourceInstall = $install; IsolatedGame = $layout.Game
    IsolatedSaveDirectory = $layout.Saves; BackupStable = $false; GameCopyVerified = $false
    CloudSyncConfirmed = $false; SavePathVerifiedInGame = $false; SafeToLaunch = $false
    Note = 'Preparation never starts a game. Stable hashes only cover this capture window, not pending cloud sync.'
}
New-SafeDirectory -Path $layout.Runtime -Root $layout.Workspace
New-SafeDirectory -Path $backup -Root $layout.Runtime
Write-WorkspaceJson -Value $status -Path $statusPath -AllowedRoot $layout.Runtime

try {
    Write-Output 'Creating a read-only save backup; source files are hashed, not parsed.'
    Assert-NoGameRunning
    $before = @(Get-SaveSnapshot)
    Write-WorkspaceJson -Value $before -Path (Join-Path $backup 'source-before.json') -AllowedRoot $layout.Runtime
    foreach ($location in $before) {
        Assert-NoGameRunning
        Copy-ManifestTree -Manifest $location.Data -Destination (Join-Path $backup $location.Id) -AllowedRoot $layout.Runtime
    }
    if ($ConsistencyDelayMilliseconds -gt 0) { Start-Sleep -Milliseconds $ConsistencyDelayMilliseconds }
    Assert-NoGameRunning
    $after = @(Get-SaveSnapshot)
    Write-WorkspaceJson -Value $after -Path (Join-Path $backup 'source-after.json') -AllowedRoot $layout.Runtime
    if (($before | ConvertTo-Json -Depth 16 -Compress) -cne ($after | ConvertTo-Json -Depth 16 -Compress)) {
        throw 'Save sources changed during backup. Partial backup is preserved; runtime preparation is blocked.'
    }
    $backupResult = [ordered]@{ Stable = $true; CapturedUtc = [DateTime]::UtcNow.ToString('o'); Sources = $before; SteamRoots = $steamRoots }
    Write-WorkspaceJson -Value $backupResult -Path (Join-Path $backup 'backup-manifest.json') -AllowedRoot $layout.Runtime
    $status.BackupStable = $true

    Write-Output 'Copying the original installation into the isolated workspace; original files remain unchanged.'
    Assert-NoGameRunning
    $gameBefore = Get-TreeManifest -Path $install
    Write-WorkspaceJson -Value $gameBefore -Path (Join-Path $backup 'game-source-before.json') -AllowedRoot $layout.Runtime
    Copy-ManifestTree -Manifest $gameBefore -Destination $layout.Game -AllowedRoot $layout.Runtime
    Assert-NoGameRunning
    $gameAfter = Get-TreeManifest -Path $install
    Write-WorkspaceJson -Value $gameAfter -Path (Join-Path $backup 'game-source-after.json') -AllowedRoot $layout.Runtime
    if (($gameBefore | ConvertTo-Json -Depth 16 -Compress) -cne ($gameAfter | ConvertTo-Json -Depth 16 -Compress)) {
        throw 'The game installation changed during copying. The copy is preserved but not approved for use.'
    }
    $copied = Get-TreeManifest -Path $layout.Game
    $expected = @($gameBefore.Files | Select-Object RelativePath, Length, SHA256)
    $actual = @($copied.Files | Select-Object RelativePath, Length, SHA256)
    if (($expected | ConvertTo-Json -Depth 8 -Compress) -cne ($actual | ConvertTo-Json -Depth 8 -Compress)) {
        throw 'The isolated game tree does not match the source installation.'
    }
    Write-WorkspaceJson -Value $copied -Path (Join-Path $backup 'game-copy-manifest.json') -AllowedRoot $layout.Runtime
    New-SafeDirectory -Path $layout.Saves -Root $layout.Runtime
    New-SafeDirectory -Path (Join-Path $layout.Runtime 'logs') -Root $layout.Runtime
    Assert-NoGameRunning
    $status.GameCopyVerified = $true
    $status.SourceGameExeSHA256 = @($gameBefore.Files | Where-Object RelativePath -EQ 'Terraria.exe')[0].SHA256
    $status.IsolatedGameExeSHA256 = @($copied.Files | Where-Object RelativePath -EQ 'Terraria.exe')[0].SHA256
    $status.ProcessVisibilityVerified = $true
    $status.Status = 'prepared_not_launched'
    $status.TerrariaFileVersion = (Get-Item -LiteralPath (Join-Path $layout.Game 'Terraria.exe')).VersionInfo.FileVersion
    $status.CompletedUtc = [DateTime]::UtcNow.ToString('o')
    Write-WorkspaceJson -Value $status -Path $statusPath -AllowedRoot $layout.Runtime
    Write-Output "Preparation complete: $($layout.Game)"
    Write-Output "Stable backup: $backup"
    Write-Output 'No game has been started. Cloud isolation and the actual SavePath still require runtime verification.'
} catch {
    $status.Status = 'failed'
    $status.Error = $_.Exception.Message
    $status.CompletedUtc = [DateTime]::UtcNow.ToString('o')
    Write-WorkspaceJson -Value $status -Path $statusPath -AllowedRoot $layout.Runtime
    Write-Output "Preparation failed; preserved data at $backup"
    throw
}
