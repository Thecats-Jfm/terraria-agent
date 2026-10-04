[CmdletBinding()]
param([ValidateRange(0, 10000)][int]$ConsistencyDelayMilliseconds = 1000)

# Refresh save evidence only. No game launch, source writes or game/save decoding.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Workspace-Safety.psm1') -Force
$newBackup = $null
$committed = $false

function Assert-Fields {
    param($Value, [string[]]$Names)
    if ($null -eq $Value) { throw 'A required manifest object is missing.' }
    foreach ($name in $Names) {
        if ($null -eq $Value.PSObject.Properties[$name]) { throw "Missing manifest field: $name" }
    }
}

function Assert-LocalPath {
    param([string]$Path)
    if ($Path -notmatch '^[A-Za-z]:[\\/]' -or $Path.Substring(2).Contains(':')) {
        throw 'Only ordinary absolute local drive paths are accepted.'
    }
    Assert-NoReparsePath -Path $Path
}

function Read-Manifest {
    param([string]$Path, [string]$Root)
    Assert-WithinRoot -Path $Path -Root $Root
    Assert-NoReparsePath -Path $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'A required successful preparation manifest is missing.' }
    try {
        $parsed = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
        # PS5 ConvertFrom-Json may emit a root JSON array as a single Object[].
        foreach ($entry in @($parsed)) { $entry }
    }
    catch { throw 'A preparation manifest could not be decoded as UTF-8 JSON.' }
}

function Get-Locations {
    param([string[]]$SteamRoots)
    $documents = [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)
    if ([string]::IsNullOrWhiteSpace($documents)) { throw 'The local Documents save location cannot be resolved.' }
    $local = [IO.Path]::GetFullPath((Join-Path $documents 'My Games/Terraria'))
    Assert-LocalPath -Path $local
    $locations = @(
        [pscustomobject]@{ Id = 'local-players'; Path = Join-Path $local 'Players'; Kind = 'Directory' },
        [pscustomobject]@{ Id = 'local-worlds'; Path = Join-Path $local 'Worlds'; Kind = 'Directory' }
    )
    $rootsSeen = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $index = 0
    foreach ($root in $SteamRoots) {
        Assert-LocalPath -Path $root
        $absolute = [IO.Path]::GetFullPath($root).TrimEnd([char[]]@('\', '/'))
        if (-not $rootsSeen.Add($absolute)) { throw 'Duplicate recorded Steam root.' }
        $userdata = Join-Path $absolute 'userdata'
        Assert-NoReparsePath -Path $userdata
        if (-not (Test-Path -LiteralPath (Join-Path $absolute 'steam.exe') -PathType Leaf) -and
            -not (Test-Path -LiteralPath $userdata -PathType Container)) { throw 'A recorded Steam root is unavailable.' }
        if (Test-Path -LiteralPath $userdata) {
            foreach ($account in @(Get-ChildItem -LiteralPath $userdata -Directory -Force | Where-Object Name -Match '^\d+$' | Sort-Object Name)) {
                Assert-NoReparsePath -Path $account.FullName
                $app = Join-Path $account.FullName '105600'
                Assert-NoReparsePath -Path $app
                if (Test-Path -LiteralPath $app) {
                    $prefix = 'cloud-{0}-{1}' -f $index, $account.Name
                    $locations += [pscustomobject]@{ Id = "$prefix-remote"; Path = Join-Path $app 'remote'; Kind = 'Directory' }
                    $locations += [pscustomobject]@{ Id = "$prefix-cache"; Path = Join-Path $app 'remotecache.vdf'; Kind = 'File' }
                }
            }
        }
        $index++
    }
    $locations | Sort-Object Id
}

function Assert-SourceSet {
    param([object[]]$Sources, [object[]]$Locations, [string]$BackupRoot)
    if ($Sources.Count -ne $Locations.Count) { throw 'The recorded save source set changed; refresh requires the same reviewed locations.' }
    $seen = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($source in $Sources) {
        Assert-Fields -Value $source -Names @('Id', 'Data')
        $id = [string]$source.Id
        if ($id -notmatch '^(local-(players|worlds)|cloud-\d+-\d+-(remote|cache))$' -or -not $seen.Add($id)) {
            throw 'An unsafe or duplicate source identifier was found.'
        }
        $location = @($Locations | Where-Object Id -EQ $id)
        if ($location.Count -ne 1) { throw 'A recorded save source is unexpected or missing.' }
        Assert-Fields -Value $source.Data -Names @('Path', 'Kind', 'Exists', 'Directories', 'Files')
        Assert-LocalPath -Path ([string]$source.Data.Path)
        if ([IO.Path]::GetFullPath([string]$source.Data.Path).TrimEnd([char[]]@('\', '/')) -ine
            $location[0].Path.TrimEnd([char[]]@('\', '/')) -or $source.Data.Kind -cne $location[0].Kind -or
            $source.Data.Exists -isnot [bool]) { throw 'A recorded source does not match the safe Documents/Steam save location.' }
        $copyRoot = Join-Path $BackupRoot $id
        Assert-WithinRoot -Path $copyRoot -Root $BackupRoot
        Assert-NoReparsePath -Path $copyRoot
        foreach ($relative in @($source.Data.Directories) + @($source.Data.Files | ForEach-Object RelativePath)) {
            $entry = [string]$relative
            if ([string]::IsNullOrWhiteSpace($entry) -or $entry.Contains('\') -or $entry.Contains(':') -or
                [IO.Path]::IsPathRooted($entry) -or @($entry.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0) {
                throw 'An unsafe relative save/backup entry was found.'
            }
            $copyEntry = [IO.Path]::GetFullPath((Join-Path $copyRoot $entry))
            Assert-WithinRoot -Path $copyEntry -Root $copyRoot
            Assert-NoReparsePath -Path $copyEntry
        }
    }
}

function Get-Snapshot {
    param([string[]]$SteamRoots, [object[]]$ReviewedSources, [string]$BackupRoot)
    Assert-NoGameRunning
    $locations = @(Get-Locations -SteamRoots $SteamRoots)
    Assert-SourceSet -Sources $ReviewedSources -Locations $locations -BackupRoot $BackupRoot
    @($locations | ForEach-Object { [pscustomobject]@{ Id = $_.Id; Data = Get-TreeManifest -Path $_.Path -Kind $_.Kind } })
}

function Get-ChangeCounts {
    param($Expected, $Current)
    $map = @{}
    foreach ($file in @($Current.Files)) { $map[[string]$file.RelativePath] = $file }
    $changed = 0; $missing = 0; $timestamps = 0
    foreach ($file in @($Expected.Files)) {
        $key = [string]$file.RelativePath
        if (-not $map.ContainsKey($key)) { $missing++; continue }
        if ($file.SHA256 -ine $map[$key].SHA256 -or [long]$file.Length -ne [long]$map[$key].Length) { $changed++ }
        if ([long]$file.LastWriteUtcTicks -ne [long]$map[$key].LastWriteUtcTicks) { $timestamps++ }
        $map.Remove($key)
    }
    [pscustomobject]@{ ChangedBytes = $changed; Missing = $missing; Added = $map.Count; WriteTimeChanges = $timestamps }
}

function Assert-CopiedBytes {
    param([object[]]$Sources, [string]$BackupRoot)
    foreach ($source in $Sources) {
        Assert-NoGameRunning
        $path = Join-Path $BackupRoot ([string]$source.Id)
        if ($source.Data.Kind -eq 'File') { $path = Join-Path $path (Split-Path -Leaf $source.Data.Path) }
        Assert-WithinRoot -Path $path -Root $BackupRoot
        Assert-NoReparsePath -Path $path
        $copy = Get-TreeManifest -Path $path -Kind $source.Data.Kind
        $expectedFiles = @($source.Data.Files | Sort-Object RelativePath | Select-Object RelativePath, Length, SHA256)
        $actualFiles = @($copy.Files | Sort-Object RelativePath | Select-Object RelativePath, Length, SHA256)
        if ($copy.Exists -ne $source.Data.Exists -or
            (ConvertTo-Json -InputObject $expectedFiles -Compress) -cne (ConvertTo-Json -InputObject $actualFiles -Compress) -or
            (ConvertTo-Json -InputObject @($source.Data.Directories | Sort-Object) -Compress) -cne
            (ConvertTo-Json -InputObject @($copy.Directories | Sort-Object) -Compress)) {
            throw 'The preserved backup files do not match their source byte/length/directory inventory.'
        }
    }
}

try {
    $layout = Get-WorkspaceLayout
    Assert-NoGameRunning
    $statusPath = Join-Path $layout.Runtime 'preparation-status.json'
    $status = Read-Manifest -Path $statusPath -Root $layout.Runtime
    $statusHash = (Get-FileHash -LiteralPath $statusPath -Algorithm SHA256).Hash
    Assert-Fields -Value $status -Names @('Status', 'BackupStable', 'BackupDirectory', 'GameCopyVerified',
        'SourceGameExeSHA256', 'IsolatedGameExeSHA256', 'CloudSyncConfirmed', 'SavePathVerifiedInGame', 'SafeToLaunch', 'ProcessVisibilityVerified')
    if ($status.Status -cne 'prepared_not_launched' -or $status.BackupStable -isnot [bool] -or -not $status.BackupStable -or
        $status.GameCopyVerified -isnot [bool] -or -not $status.GameCopyVerified -or
        $status.SourceGameExeSHA256 -cne $status.IsolatedGameExeSHA256 -or
        [string]$status.SourceGameExeSHA256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'A successful verified game copy and prior backup are required.' }
    $gameExe = Join-Path $layout.Game 'Terraria.exe'
    Assert-WithinRoot -Path $gameExe -Root $layout.Runtime
    Assert-NoReparsePath -Path $gameExe
    if ((Get-FileHash -LiteralPath $gameExe -Algorithm SHA256).Hash -ine $status.SourceGameExeSHA256) {
        throw 'The isolated game executable no longer matches the originally verified hash.'
    }
    $backupsRoot = [IO.Path]::GetFullPath((Join-Path $layout.Runtime 'backups'))
    Assert-LocalPath -Path ([string]$status.BackupDirectory)
    $oldBackup = [IO.Path]::GetFullPath([string]$status.BackupDirectory).TrimEnd([char[]]@('\', '/'))
    Assert-WithinRoot -Path $oldBackup -Root $backupsRoot
    if ((Split-Path -Parent $oldBackup) -ine $backupsRoot -or
        (Split-Path -Leaf $oldBackup) -notmatch '^\d{8}T\d{6}Z-[A-Fa-f0-9]{8}$') { throw 'The prior backup is outside the reviewed backup layout.' }
    $oldManifest = Read-Manifest -Path (Join-Path $oldBackup 'backup-manifest.json') -Root $oldBackup
    $oldBefore = @(Read-Manifest -Path (Join-Path $oldBackup 'source-before.json') -Root $oldBackup)
    $oldAfter = @(Read-Manifest -Path (Join-Path $oldBackup 'source-after.json') -Root $oldBackup)
    Assert-Fields -Value $oldManifest -Names @('Stable', 'Sources', 'SteamRoots')
    if ($oldManifest.Stable -isnot [bool] -or -not $oldManifest.Stable -or @($oldManifest.SteamRoots).Count -eq 0) {
        throw 'The prior backup lacks stable source and Steam coverage evidence.'
    }
    $locations = @(Get-Locations -SteamRoots @($oldManifest.SteamRoots))
    foreach ($sourceSet in @(@{Sources=@($oldManifest.Sources)}, @{Sources=$oldBefore}, @{Sources=$oldAfter})) {
        Assert-SourceSet -Sources $sourceSet.Sources -Locations $locations -BackupRoot $oldBackup
    }
    $oldCanonical = ConvertTo-Json -InputObject @($oldManifest.Sources) -Depth 16 -Compress
    if ($oldCanonical -cne (ConvertTo-Json -InputObject $oldBefore -Depth 16 -Compress) -or
        $oldCanonical -cne (ConvertTo-Json -InputObject $oldAfter -Depth 16 -Compress)) { throw 'The prior stable source manifests are inconsistent.' }
    Assert-CopiedBytes -Sources @($oldManifest.Sources) -BackupRoot $oldBackup

    $attempt = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $newBackup = Join-Path $backupsRoot $attempt
    Assert-WithinRoot -Path $newBackup -Root $backupsRoot
    Assert-NoReparsePath -Path $newBackup
    if (Test-Path -LiteralPath $newBackup) { throw 'The new backup identifier already exists; nothing will be overwritten.' }
    New-SafeDirectory -Path $newBackup -Root $layout.Runtime
    $priorStatusPath = Join-Path $newBackup 'previous-preparation-status.json'
    Assert-NoReparsePath -Path $priorStatusPath
    [IO.File]::Copy($statusPath, $priorStatusPath, $false)
    $before = @(Get-Snapshot -SteamRoots @($oldManifest.SteamRoots) -ReviewedSources @($oldManifest.Sources) -BackupRoot $newBackup)
    Write-WorkspaceJson -Value $before -Path (Join-Path $newBackup 'source-before.json') -AllowedRoot $layout.Runtime
    foreach ($source in $before) {
        Assert-NoGameRunning
        Copy-ManifestTree -Manifest $source.Data -Destination (Join-Path $newBackup $source.Id) -AllowedRoot $layout.Runtime
    }
    Assert-CopiedBytes -Sources $before -BackupRoot $newBackup
    if ($ConsistencyDelayMilliseconds -gt 0) { Start-Sleep -Milliseconds $ConsistencyDelayMilliseconds }
    $after = @(Get-Snapshot -SteamRoots @($oldManifest.SteamRoots) -ReviewedSources @($oldManifest.Sources) -BackupRoot $newBackup)
    Write-WorkspaceJson -Value $after -Path (Join-Path $newBackup 'source-after.json') -AllowedRoot $layout.Runtime
    if ((ConvertTo-Json -InputObject $before -Depth 16 -Compress) -cne
        (ConvertTo-Json -InputObject $after -Depth 16 -Compress)) { throw 'Original save sources changed during refresh; the new partial backup is retained.' }
    $changes = @($before | ForEach-Object {
        $source = $_
        $oldSource = @($oldManifest.Sources | Where-Object Id -EQ $source.Id)[0]
        $category = switch -Wildcard ($source.Id) {
            'local-players' { 'LocalPlayers' } 'local-worlds' { 'LocalWorlds' }
            '*-remote' { 'SteamCloudFiles' } '*-cache' { 'SteamCloudMetadata' }
        }
        [pscustomobject]@{ Category = $category; Counts = Get-ChangeCounts -Expected $oldSource.Data -Current $source.Data }
    })
    Write-WorkspaceJson -Value ([ordered]@{ Stable = $true; CapturedUtc = [DateTime]::UtcNow.ToString('o');
        Sources = $before; SteamRoots = @($oldManifest.SteamRoots); PreviousBackupId = Split-Path -Leaf $oldBackup;
        ChangesFromPriorBackup = $changes; GameCopyReused = $true }) -Path (Join-Path $newBackup 'backup-manifest.json') -AllowedRoot $layout.Runtime
    Assert-NoGameRunning
    if ((Get-FileHash -LiteralPath $gameExe -Algorithm SHA256).Hash -ine $status.SourceGameExeSHA256) { throw 'The isolated game executable changed during refresh.' }
    if ((Get-FileHash -LiteralPath $statusPath -Algorithm SHA256).Hash -cne $statusHash) { throw 'The preparation status changed concurrently; refusing to replace it.' }
    $status.BackupDirectory = $newBackup
    $status.BackupStable = $true
    $status.ProcessVisibilityVerified = $true
    $status.CloudSyncConfirmed = $false
    $status.SavePathVerifiedInGame = $false
    $status.SafeToLaunch = $false
    $status | Add-Member -NotePropertyName LastBackupRefreshUtc -NotePropertyValue ([DateTime]::UtcNow.ToString('o')) -Force
    $pendingStatus = Join-Path $newBackup 'next-preparation-status.json'
    Write-WorkspaceJson -Value $status -Path $pendingStatus -AllowedRoot $layout.Runtime
    Assert-NoReparsePath -Path $statusPath
    # Same-volume atomic replacement: any failure before this leaves the prior
    # successful status untouched. Its exact bytes are also retained above.
    # A real backup path avoids PS5 coercing a null string argument to "".
    $replacedStatus = Join-Path $newBackup 'replaced-preparation-status.json'
    Assert-NoReparsePath -Path $replacedStatus
    if (Test-Path -LiteralPath $replacedStatus) { throw 'The atomic status backup path already exists.' }
    [IO.File]::Replace($pendingStatus, $statusPath, $replacedStatus)
    $committed = $true
    [pscustomobject]@{ Status = 'refreshed_stable_backup'; BackupId = $attempt; SourceCount = $before.Count;
        FileCount = (@($before | ForEach-Object { @($_.Data.Files).Count }) | Measure-Object -Sum).Sum;
        ChangesFromPriorBackup = $changes; OriginalFilesWritten = $false; GameFilesWritten = $false;
        PreviousBackupPreserved = $true; CloudSyncConfirmed = $false; GameStarted = $false }
} catch {
    $reason = $_.Exception.Message
    if ($null -ne $newBackup -and (Test-Path -LiteralPath $newBackup) -and -not $committed) {
        try { Write-WorkspaceJson -Value ([ordered]@{ Status = 'failed_preserved'; Error = $reason;
            FailedUtc = [DateTime]::UtcNow.ToString('o'); PriorPreparationUnchanged = $true; GameStarted = $false }) `
            -Path (Join-Path $newBackup 'refresh-failure.json') -AllowedRoot $layout.Runtime } catch { }
    }
    throw "Backup refresh blocked: $reason Existing original saves, game files and prior backups were not overwritten; partial new evidence is preserved."
}
