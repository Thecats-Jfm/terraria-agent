[CmdletBinding()]
param([ValidateSet('main','combat_test')][string]$Challenge = 'main')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Workspace-Safety.psm1') -Force
Assert-NoGameRunning
$taskLayout = Get-WorkspaceLayout
$taskSaveRoot = [IO.Path]::GetFullPath((Join-Path $taskLayout.Runtime ('saves/' + $Challenge)))
Assert-WithinRoot -Path $taskSaveRoot -Root (Join-Path $taskLayout.Runtime 'saves')
Assert-NoReparsePath -Path $taskSaveRoot
$taskProfilePath = Join-Path $taskSaveRoot 'input profiles.json'
$taskConfigPath = Join-Path $taskSaveRoot 'config.json'
foreach ($taskPath in @($taskProfilePath, $taskConfigPath)) {
    Assert-WithinRoot -Path $taskPath -Root $taskSaveRoot
    Assert-NoReparsePath -Path $taskPath
}
if (-not (Test-Path -LiteralPath $taskProfilePath -PathType Leaf)) {
    throw 'No isolated input profiles.json exists. This script never creates or guesses a profile.'
}
if ((Get-Item -LiteralPath $taskProfilePath).Length -gt 4194304) { throw 'Unexpected input profile size.' }
$taskOriginalHash = (Get-FileHash -LiteralPath $taskProfilePath -Algorithm SHA256).Hash
$taskProfiles = Get-Content -LiteralPath $taskProfilePath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($null -eq $taskProfiles.PSObject.Properties['Selected Profile'] -or
    $taskProfiles.'Selected Profile' -cne 'Custom') {
    throw 'The selected isolated profile must be Custom; no other profile will be changed.'
}
$taskCustomProperty = $taskProfiles.PSObject.Properties['Custom']
if ($null -eq $taskCustomProperty -or $null -eq $taskCustomProperty.Value) { throw 'Custom profile is absent.' }
$taskCustom = $taskCustomProperty.Value
if ($null -eq $taskCustom.PSObject.Properties['Settings'] -or
    $null -eq $taskCustom.Settings.PSObject.Properties['Edittable'] -or
    $taskCustom.Settings.Edittable -isnot [bool] -or -not $taskCustom.Settings.Edittable) {
    throw 'Custom must be an explicitly editable existing profile (Settings.Edittable=true).'
}
if ($null -eq $taskCustom.PSObject.Properties['Mouse And Keyboard']) { throw 'Custom keyboard bindings are absent.' }
$taskKeyboard = $taskCustom.'Mouse And Keyboard'
foreach ($taskName in @('SmartCursor','SmartSelect')) {
    if ($null -eq $taskKeyboard.PSObject.Properties[$taskName]) { throw "Missing existing binding: $taskName" }
    if ($taskKeyboard.PSObject.Properties[$taskName].Value -isnot [System.Array]) {
        throw "Expected a JSON array for binding: $taskName"
    }
    foreach ($taskKey in $taskKeyboard.PSObject.Properties[$taskName].Value) {
        if ($taskKey -isnot [string]) { throw "Non-string keyboard binding: $taskName" }
    }
}
$taskOldCursor = @($taskKeyboard.SmartCursor)
$taskOldSelect = @($taskKeyboard.SmartSelect)
$taskOriginalCanonical = $taskProfiles | ConvertTo-Json -Depth 100 -Compress
$taskKeyboard.SmartCursor = @('RightControl')
$taskKeyboard.SmartSelect = @('RightShift')
$taskNewJson = $taskProfiles | ConvertTo-Json -Depth 100
$taskRoundtrip = $taskNewJson | ConvertFrom-Json
$taskRoundtripKeyboard = $taskRoundtrip.Custom.'Mouse And Keyboard'
if (@($taskRoundtripKeyboard.SmartCursor).Count -ne 1 -or $taskRoundtripKeyboard.SmartCursor[0] -cne 'RightControl' -or
    @($taskRoundtripKeyboard.SmartSelect).Count -ne 1 -or $taskRoundtripKeyboard.SmartSelect[0] -cne 'RightShift') {
    throw 'Profile JSON did not preserve the intended binding arrays.'
}
# Reinsert the old values into the parsed candidate. Everything else must
# round-trip identically before any bytes in the isolated profile are replaced.
$taskRoundtripKeyboard.SmartCursor = $taskOldCursor
$taskRoundtripKeyboard.SmartSelect = $taskOldSelect
if (($taskRoundtrip | ConvertTo-Json -Depth 100 -Compress) -cne $taskOriginalCanonical) {
    throw 'JSON round-trip changed another profile/control setting; no profile was written.'
}
$taskStamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$taskBackupRoot = Join-Path $taskLayout.Runtime ('logs/profile-config-' + $taskStamp)
New-SafeDirectory -Path $taskBackupRoot -Root $taskLayout.Runtime
foreach ($taskPath in @($taskProfilePath, $taskConfigPath)) {
    if (Test-Path -LiteralPath $taskPath -PathType Leaf) {
        $taskBackupPath = Join-Path $taskBackupRoot (Split-Path -Leaf $taskPath)
        Assert-NoReparsePath -Path $taskBackupPath
        Copy-Item -LiteralPath $taskPath -Destination $taskBackupPath
        if ((Get-FileHash -LiteralPath $taskPath -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $taskBackupPath -Algorithm SHA256).Hash) {
            throw 'Isolated profile/config backup bytes do not match; nothing was written.'
        }
    }
}
Assert-NoGameRunning
Assert-NoReparsePath -Path $taskProfilePath
if ((Get-FileHash -LiteralPath $taskProfilePath -Algorithm SHA256).Hash -ne $taskOriginalHash) {
    throw 'Profile changed during preparation; refusing to overwrite it.'
}
$taskTemporary = Join-Path $taskSaveRoot ('input-profiles-' + $taskStamp + '.tmp')
Assert-WithinRoot -Path $taskTemporary -Root $taskSaveRoot
Assert-NoReparsePath -Path $taskTemporary
try {
    [IO.File]::WriteAllText($taskTemporary, $taskNewJson, (New-Object Text.UTF8Encoding($false)))
    Assert-NoGameRunning
    if ((Get-FileHash -LiteralPath $taskProfilePath -Algorithm SHA256).Hash -ne $taskOriginalHash) {
        throw 'Profile changed before replacement; refusing to overwrite it.'
    }
    # Windows PowerShell 5.1 coerces a null string argument to an empty path.
    # Give File.Replace its own explicit backup path instead.
    $taskAtomicBackup = Join-Path $taskBackupRoot 'input-profiles.atomic.json'
    Assert-WithinRoot -Path $taskAtomicBackup -Root $taskBackupRoot
    Assert-NoReparsePath -Path $taskAtomicBackup
    [IO.File]::Replace($taskTemporary, $taskProfilePath, $taskAtomicBackup)
    if ((Get-FileHash -LiteralPath $taskAtomicBackup -Algorithm SHA256).Hash -ne $taskOriginalHash) {
        throw 'Atomic replacement backup differs; retain the explicit backups before launch.'
    }
    $taskWritten = Get-Content -LiteralPath $taskProfilePath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($taskWritten.'Selected Profile' -cne 'Custom' -or
        @($taskWritten.Custom.'Mouse And Keyboard'.SmartCursor).Count -ne 1 -or
        $taskWritten.Custom.'Mouse And Keyboard'.SmartCursor[0] -cne 'RightControl' -or
        @($taskWritten.Custom.'Mouse And Keyboard'.SmartSelect).Count -ne 1 -or
        $taskWritten.Custom.'Mouse And Keyboard'.SmartSelect[0] -cne 'RightShift') {
        throw 'Written profile verification failed; retain and restore the explicit backup before launch.'
    }
} finally {
    if (Test-Path -LiteralPath $taskTemporary -PathType Leaf) { Remove-Item -LiteralPath $taskTemporary }
}
[pscustomobject]@{
    Status = 'isolated_custom_bindings_updated'
    Challenge = $Challenge
    SelectedProfile = 'Custom'
    SmartCursorBefore = $taskOldCursor
    SmartCursorAfter = @('RightControl')
    SmartSelectBefore = $taskOldSelect
    SmartSelectAfter = @('RightShift')
    BackupDirectory = $taskBackupRoot
    ConfigModified = $false
    GameplayVerified = $false
}
