[CmdletBinding()]
param()

# Read-only gate. File contents are hashed, never decoded as game saves.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Workspace-Safety.psm1') -Force

function Assert-RequiredFields {
    param($Value, [string[]]$Names)
    if ($null -eq $Value) { throw 'A required manifest object is missing.' }
    foreach ($name in $Names) {
        if ($null -eq $Value.PSObject.Properties[$name]) { throw "A required manifest field is missing: $name" }
    }
}

function Assert-PlainAbsolutePath {
    param([string]$Path)
    # Only ordinary, absolute Windows drive paths are valid in this local workflow.
    if ($Path -notmatch '^[A-Za-z]:[\\/]' -or $Path.Substring(2).Contains(':')) {
        throw 'A manifest contains a nonlocal, relative or alternate-stream path.'
    }
    Assert-NoReparsePath -Path $Path
}

function Read-SafeJson {
    param([string]$Path, [string]$Root)
    Assert-WithinRoot -Path $Path -Root $Root
    Assert-NoReparsePath -Path $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'A required preparation manifest is missing.' }
    $taskParsed = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($taskEntry in @($taskParsed)) { $taskEntry }
}

function Assert-RelativeEntry {
    param([string]$Entry, [string]$Root)
    if ([string]::IsNullOrWhiteSpace($Entry) -or $Entry.Contains('\') -or $Entry.Contains(':') -or
        [IO.Path]::IsPathRooted($Entry) -or @($Entry.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0) {
        throw 'A manifest contains an unsafe relative entry.'
    }
    $entryPath = [IO.Path]::GetFullPath((Join-Path $Root $Entry))
    Assert-WithinRoot -Path $entryPath -Root $Root
    Assert-NoReparsePath -Path $entryPath
}

function Assert-Inventory {
    param($Inventory, [string]$SourcePath, [string]$Kind, [string]$BackupPath)
    Assert-RequiredFields -Value $Inventory -Names @('Path', 'Kind', 'Exists', 'Directories', 'Files')
    Assert-PlainAbsolutePath -Path ([string]$Inventory.Path)
    if ([IO.Path]::GetFullPath([string]$Inventory.Path).TrimEnd([char[]]@('\', '/')) -ine
        $SourcePath.TrimEnd([char[]]@('\', '/')) -or $Inventory.Kind -cne $Kind -or $Inventory.Exists -isnot [bool]) {
        throw 'A source manifest does not match the expected save location or type.'
    }
    $fileNames = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $directoryNames = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($Inventory.Directories)) {
        Assert-RelativeEntry -Entry ([string]$entry) -Root $SourcePath
        Assert-RelativeEntry -Entry ([string]$entry) -Root $BackupPath
        if (-not $directoryNames.Add([string]$entry)) { throw 'A manifest repeats a directory entry.' }
    }
    foreach ($file in @($Inventory.Files)) {
        Assert-RequiredFields -Value $file -Names @('RelativePath', 'Length', 'LastWriteUtcTicks', 'SHA256')
        $relative = [string]$file.RelativePath
        $sourceDirectory = if ($Kind -eq 'File') { Split-Path -Parent $SourcePath } else { $SourcePath }
        Assert-RelativeEntry -Entry $relative -Root $sourceDirectory
        Assert-RelativeEntry -Entry $relative -Root $BackupPath
        if ($Kind -eq 'File' -and $relative -cne (Split-Path -Leaf $SourcePath)) { throw 'The single-file manifest has an unexpected name.' }
        if (-not $fileNames.Add($relative) -or [string]$file.SHA256 -notmatch '^[A-Fa-f0-9]{64}$' -or
            [string]$file.Length -notmatch '^(0|[1-9][0-9]*)$' -or [string]$file.LastWriteUtcTicks -notmatch '^(0|[1-9][0-9]*)$') {
            throw 'A manifest has a duplicate entry or invalid file metadata/hash.'
        }
        [long]$file.Length | Out-Null
        [long]$file.LastWriteUtcTicks | Out-Null
    }
    if (($Kind -eq 'File' -and (@($Inventory.Files).Count -gt 1 -or @($Inventory.Directories).Count -ne 0)) -or
        (-not $Inventory.Exists -and (@($Inventory.Files).Count -ne 0 -or @($Inventory.Directories).Count -ne 0))) {
        throw 'A manifest has an inconsistent source inventory.'
    }
}

function Assert-SameInventory {
    param($Expected, $Actual, [string]$Label, [switch]$CheckWriteTime)
    $actualByName = @{}
    foreach ($file in @($Actual.Files)) { $actualByName[[string]$file.RelativePath] = $file }
    $changed = 0; $missing = 0; $metadataChanged = 0
    foreach ($file in @($Expected.Files)) {
        $relative = [string]$file.RelativePath
        if (-not $actualByName.ContainsKey($relative)) { $missing++; continue }
        $current = $actualByName[$relative]
        if ($file.SHA256 -ine $current.SHA256 -or [long]$file.Length -ne [long]$current.Length) { $changed++ }
        if ($CheckWriteTime -and [long]$file.LastWriteUtcTicks -ne [long]$current.LastWriteUtcTicks) { $metadataChanged++ }
        $actualByName.Remove($relative)
    }
    $expectedDirectories = @($Expected.Directories | Sort-Object)
    $actualDirectories = @($Actual.Directories | Sort-Object)
    $directoriesDiffer = (ConvertTo-Json -InputObject $expectedDirectories -Compress) -cne
        (ConvertTo-Json -InputObject $actualDirectories -Compress)
    if ($Expected.Exists -ne $Actual.Exists -or $changed -gt 0 -or $missing -gt 0 -or
        $actualByName.Count -gt 0 -or $metadataChanged -gt 0 -or $directoriesDiffer) {
        throw "$Label differs from the successful backup: changed bytes=$changed, missing=$missing, added=$($actualByName.Count), write-time changes=$metadataChanged, directory/existence change=$($directoriesDiffer -or ($Expected.Exists -ne $Actual.Exists))."
    }
}

function Get-ExpectedLocations {
    param([string[]]$SteamRoots)
    $documents = [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)
    if ([string]::IsNullOrWhiteSpace($documents)) { throw 'The Documents save location could not be resolved.' }
    $localRoot = [IO.Path]::GetFullPath((Join-Path $documents 'My Games/Terraria'))
    Assert-PlainAbsolutePath -Path $localRoot
    $locations = @(
        [pscustomobject]@{ Id = 'local-players'; Path = Join-Path $localRoot 'Players'; Kind = 'Directory' },
        [pscustomobject]@{ Id = 'local-worlds'; Path = Join-Path $localRoot 'Worlds'; Kind = 'Directory' }
    )
    $seenRoots = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $index = 0
    foreach ($root in $SteamRoots) {
        Assert-PlainAbsolutePath -Path $root
        $absolute = [IO.Path]::GetFullPath($root).TrimEnd([char[]]@('\', '/'))
        if (-not $seenRoots.Add($absolute)) { throw 'A backup repeats a Steam installation root.' }
        $userdata = Join-Path $absolute 'userdata'
        Assert-NoReparsePath -Path $userdata
        if (-not (Test-Path -LiteralPath (Join-Path $absolute 'steam.exe') -PathType Leaf) -and
            -not (Test-Path -LiteralPath $userdata -PathType Container)) { throw 'A recorded Steam installation is unavailable.' }
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

function Get-SourceMap {
    param([object[]]$Sources, [object[]]$Locations, [string]$BackupRoot)
    if ($Sources.Count -ne $Locations.Count) { throw 'The original save source set changed or a manifest source is missing.' }
    $map = @{}
    foreach ($source in $Sources) {
        Assert-RequiredFields -Value $source -Names @('Id', 'Data')
        $id = [string]$source.Id
        if ($id -notmatch '^(local-(players|worlds)|cloud-\d+-\d+-(remote|cache))$' -or $map.ContainsKey($id)) {
            throw 'A backup has an unsafe or repeated source identifier.'
        }
        $location = @($Locations | Where-Object Id -EQ $id)
        if ($location.Count -ne 1) { throw 'The original save source set changed or a manifest source is unexpected.' }
        $copyRoot = Join-Path $BackupRoot $id
        Assert-WithinRoot -Path $copyRoot -Root $BackupRoot
        Assert-NoReparsePath -Path $copyRoot
        Assert-Inventory -Inventory $source.Data -SourcePath $location[0].Path -Kind $location[0].Kind -BackupPath $copyRoot
        $map[$id] = $source.Data
    }
    return $map
}

try {
    $layout = Get-WorkspaceLayout
    $statusPath = Join-Path $layout.Runtime 'preparation-status.json'
    $status = Read-SafeJson -Path $statusPath -Root $layout.Runtime
    Assert-RequiredFields -Value $status -Names @('Status', 'BackupStable', 'BackupDirectory')
    if ($status.Status -cne 'prepared_not_launched' -or $status.BackupStable -isnot [bool] -or -not $status.BackupStable) {
        throw 'Preparation does not identify a successful, stable backup.'
    }
    $backupsRoot = [IO.Path]::GetFullPath((Join-Path $layout.Runtime 'backups'))
    Assert-PlainAbsolutePath -Path ([string]$status.BackupDirectory)
    $backupRoot = [IO.Path]::GetFullPath([string]$status.BackupDirectory).TrimEnd([char[]]@('\', '/'))
    Assert-WithinRoot -Path $backupRoot -Root $backupsRoot
    if ((Split-Path -Parent $backupRoot) -ine $backupsRoot -or
        (Split-Path -Leaf $backupRoot) -notmatch '^\d{8}T\d{6}Z-[a-fA-F0-9]{8}$') {
        throw 'The prepared backup must be a named direct child of the fixed runtime backups directory.'
    }
    $manifest = Read-SafeJson -Path (Join-Path $backupRoot 'backup-manifest.json') -Root $backupRoot
    $before = @(Read-SafeJson -Path (Join-Path $backupRoot 'source-before.json') -Root $backupRoot)
    $after = @(Read-SafeJson -Path (Join-Path $backupRoot 'source-after.json') -Root $backupRoot)
    Assert-RequiredFields -Value $manifest -Names @('Stable', 'Sources', 'SteamRoots')
    if ($manifest.Stable -isnot [bool] -or -not $manifest.Stable -or @($manifest.SteamRoots).Count -eq 0) {
        throw 'The backup manifest does not record a stable capture with Steam save coverage.'
    }
    $locations = @(Get-ExpectedLocations -SteamRoots @($manifest.SteamRoots))
    $recorded = Get-SourceMap -Sources @($manifest.Sources) -Locations $locations -BackupRoot $backupRoot
    $recordedBefore = Get-SourceMap -Sources $before -Locations $locations -BackupRoot $backupRoot
    $recordedAfter = Get-SourceMap -Sources $after -Locations $locations -BackupRoot $backupRoot
    $fileCount = 0
    $timestampOnlyChanges = 0
    $firstSamples = @{}
    foreach ($location in $locations) {
        $id = [string]$location.Id
        Assert-SameInventory -Expected $recorded[$id] -Actual $recordedBefore[$id] -Label 'Before-capture manifest' -CheckWriteTime
        Assert-SameInventory -Expected $recorded[$id] -Actual $recordedAfter[$id] -Label 'After-capture manifest' -CheckWriteTime
        $current = Get-TreeManifest -Path $location.Path -Kind $location.Kind
        Assert-SameInventory -Expected $recorded[$id] -Actual $current -Label 'Original save source'
        $firstSamples[$id] = $current
        $currentByName = @{}
        foreach ($file in @($current.Files)) { $currentByName[[string]$file.RelativePath] = $file }
        foreach ($file in @($recorded[$id].Files)) {
            if ([long]$file.LastWriteUtcTicks -ne [long]$currentByName[[string]$file.RelativePath].LastWriteUtcTicks) { $timestampOnlyChanges++ }
        }
        $fileCount += @($recorded[$id].Files).Count
        $copyPath = Join-Path $backupRoot $id
        if ($location.Kind -eq 'File') { $copyPath = Join-Path $copyPath (Split-Path -Leaf $location.Path) }
        Assert-WithinRoot -Path $copyPath -Root $backupRoot
        Assert-NoReparsePath -Path $copyPath
        $copy = Get-TreeManifest -Path $copyPath -Kind $location.Kind
        Assert-SameInventory -Expected $recorded[$id] -Actual $copy -Label 'Preserved backup copy'
    }
    # Check again after reading copies: pending synchronization can change sources
    # while verification is running. This is a bounded capture, not a sync claim.
    $locationsAgain = @(Get-ExpectedLocations -SteamRoots @($manifest.SteamRoots))
    $null = Get-SourceMap -Sources @($manifest.Sources) -Locations $locationsAgain -BackupRoot $backupRoot
    foreach ($location in $locationsAgain) {
        $current = Get-TreeManifest -Path $location.Path -Kind $location.Kind
        Assert-SameInventory -Expected $recorded[[string]$location.Id] -Actual $current -Label 'Original save source after verification'
        Assert-SameInventory -Expected $firstSamples[[string]$location.Id] -Actual $current -Label 'Original source stability during verification' -CheckWriteTime
    }
    [pscustomobject]@{
        Status = 'verified_read_only'; CheckedUtc = [DateTime]::UtcNow.ToString('o')
        SourceCount = $locations.Count; FileCount = $fileCount; ChangedFiles = 0
        MetadataOnlyTimestampChanges = $timestampOnlyChanges
        OriginalFilesWritten = $false; BackupFilesWritten = $false; CloudSyncConfirmed = $false
    }
} catch {
    throw ("Backup verification blocked: {0} Create a new, separate verified save backup before starting, or resolve the manifest/path error. This check never overwrites original saves, preserved backups or preparation status." -f $_.Exception.Message)
}
