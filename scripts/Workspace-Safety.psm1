Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-WorkspaceLayout {
    $project = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
    $outputs = Split-Path -Parent $project
    if ((Split-Path -Leaf $outputs) -ne 'outputs' -or
        -not (Test-Path -LiteralPath (Join-Path $project 'AGENTS.md')) -or
        -not (Test-Path -LiteralPath (Join-Path $project 'docs/TONIGHT_PLAN.md'))) {
        throw 'Expected this repository under the workspace outputs directory with its project instructions.'
    }
    $workspace = [IO.Path]::GetFullPath((Split-Path -Parent $outputs))
    $runtime = [IO.Path]::GetFullPath((Join-Path $workspace 'work/terraria-runtime'))
    Assert-WithinRoot -Path $runtime -Root $workspace
    Assert-NoReparsePath -Path $runtime
    [pscustomobject]@{
        Project = $project
        Workspace = $workspace
        Runtime = $runtime
        Game = Join-Path $runtime 'game'
        Saves = Join-Path $runtime 'saves/main'
        Feed = Join-Path $workspace 'work/dependencies/feed'
        Packages = Join-Path $runtime 'nuget-packages'
        Build = Join-Path $runtime 'build'
    }
}

function Assert-WithinRoot {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Root)
    if (-not [IO.Path]::IsPathRooted($Path) -or -not [IO.Path]::IsPathRooted($Root)) {
        throw 'Safety checks require absolute paths.'
    }
    $absolute = [IO.Path]::GetFullPath($Path)
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd([char[]]@('\', '/'))
    $prefix = $rootPath + [IO.Path]::DirectorySeparatorChar
    if (-not $absolute.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase) -and
        -not $absolute.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Destination is outside the required workspace root: $absolute"
    }
}

function Assert-NoReparsePath {
    param([Parameter(Mandatory)][string]$Path)
    $cursor = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrEmpty($cursor)) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing a symbolic link or directory junction: $cursor"
            }
        }
        $parent = Split-Path -Parent $cursor
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
}

function New-SafeDirectory {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Root)
    Assert-WithinRoot -Path $Path -Root $Root
    Assert-NoReparsePath -Path $Path
    [IO.Directory]::CreateDirectory($Path) | Out-Null
    Assert-NoReparsePath -Path $Path
}

function Assert-NoGameRunning {
    param([switch]$RequireDesktopVisibility = $true)
    if ($RequireDesktopVisibility) {
        $desktopProcesses = @(Get-Process -Name explorer, steam, Codex -ErrorAction SilentlyContinue)
        if ($desktopProcesses.Count -eq 0) {
            throw 'Desktop process visibility could not be established. A sandbox may hide the running game; preparation is blocked until run in a desktop-visible context.'
        }
    }
    $names = @('Terraria', 'TerrariaServer', 'tModLoader', 'TerrariaInjector', 'AgentHost',
        'TerrariaAgentHost', 'TerrariaAgent.Host')
    $running = @(Get-Process -Name $names -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        $description = ($running | ForEach-Object { '{0} (PID {1})' -f $_.ProcessName, $_.Id }) -join ', '
        throw "Close the game and game host before preparing or building: $description"
    }
}

function Get-TreeManifest {
    param([Parameter(Mandatory)][string]$Path,
        [ValidateSet('Directory', 'File')][string]$Kind = 'Directory')
    $absolute = [IO.Path]::GetFullPath($Path)
    Assert-NoReparsePath -Path $absolute
    $present = Test-Path -LiteralPath $absolute
    if (-not $present) {
        return [pscustomobject]@{ Path = $absolute; Kind = $Kind; Exists = $false; Directories = @(); Files = @() }
    }
    $rootItem = Get-Item -LiteralPath $absolute -Force
    if (($Kind -eq 'Directory') -ne $rootItem.PSIsContainer) {
        throw "Unexpected source path type: $absolute"
    }
    if ($Kind -eq 'Directory') {
        $items = @(Get-ChildItem -LiteralPath $absolute -Force -Recurse -ErrorAction Stop)
    } else { $items = @($rootItem) }
    $links = @($items | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
    if ($links.Count -gt 0) { throw "Refusing links within source tree: $($links[0].FullName)" }
    $directories = @($items | Where-Object PSIsContainer | ForEach-Object {
        $_.FullName.Substring($absolute.Length).TrimStart([char[]]@('\', '/')).Replace('\', '/')
    } | Sort-Object)
    $files = @($items | Where-Object { -not $_.PSIsContainer } | Sort-Object FullName | ForEach-Object {
        $relative = if ($Kind -eq 'File') { $rootItem.Name } else {
            $_.FullName.Substring($absolute.Length).TrimStart([char[]]@('\', '/')).Replace('\', '/')
        }
        $digest = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256 -ErrorAction Stop).Hash
        $_.Refresh()
        [pscustomobject]@{
            RelativePath = $relative
            Length = [long]$_.Length
            LastWriteUtcTicks = $_.LastWriteTimeUtc.Ticks
            SHA256 = $digest
        }
    })
    [pscustomobject]@{ Path = $absolute; Kind = $Kind; Exists = $true; Directories = $directories; Files = $files }
}

function Copy-ManifestTree {
    param([Parameter(Mandatory)]$Manifest, [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$AllowedRoot)
    Assert-WithinRoot -Path $Destination -Root $AllowedRoot
    Assert-NoReparsePath -Path $Destination
    if (-not $Manifest.Exists) { return }
    New-SafeDirectory -Path $Destination -Root $AllowedRoot
    foreach ($relative in $Manifest.Directories) {
        New-SafeDirectory -Path (Join-Path $Destination $relative) -Root $AllowedRoot
    }
    $checkedSourceDirectories = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $checkedTargetDirectories = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $Manifest.Files) {
        $source = if ($Manifest.Kind -eq 'File') { $Manifest.Path } else { Join-Path $Manifest.Path $file.RelativePath }
        $target = Join-Path $Destination $file.RelativePath
        Assert-WithinRoot -Path $target -Root $AllowedRoot
        $sourceDirectory = Split-Path -Parent $source
        $targetDirectory = Split-Path -Parent $target
        if ($checkedSourceDirectories.Add($sourceDirectory)) { Assert-NoReparsePath -Path $sourceDirectory }
        if ($checkedTargetDirectories.Add($targetDirectory)) { New-SafeDirectory -Path $targetDirectory -Root $AllowedRoot }
        $sourceItem = Get-Item -LiteralPath $source -Force
        if (($sourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Source became a symbolic link: $source"
        }
        if ($null -ne (Get-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue)) {
            throw "Refusing to overwrite copied data: $target"
        }
        Copy-Item -LiteralPath $source -Destination $target -ErrorAction Stop
        $copy = Get-Item -LiteralPath $target
        $digest = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
        if ($copy.Length -ne $file.Length -or $digest -ne $file.SHA256) {
            throw "Copied bytes do not match the source manifest: $source"
        }
    }
}

function Write-WorkspaceJson {
    param([Parameter(Mandatory)]$Value, [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$AllowedRoot)
    Assert-WithinRoot -Path $Path -Root $AllowedRoot
    Assert-NoReparsePath -Path $Path
    New-SafeDirectory -Path (Split-Path -Parent $Path) -Root $AllowedRoot
    $json = $Value | ConvertTo-Json -Depth 16
    [IO.File]::WriteAllText($Path, $json, (New-Object Text.UTF8Encoding($false)))
}

Export-ModuleMember -Function Get-WorkspaceLayout, Assert-WithinRoot, Assert-NoReparsePath,
    New-SafeDirectory, Assert-NoGameRunning, Get-TreeManifest, Copy-ManifestTree, Write-WorkspaceJson
