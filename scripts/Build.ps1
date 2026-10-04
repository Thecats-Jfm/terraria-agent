[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Workspace-Safety.psm1') -Force
$layout = Get-WorkspaceLayout
Assert-NoGameRunning -RequireDesktopVisibility:$false
if (-not (Test-Path -LiteralPath (Join-Path $layout.Game 'Terraria.exe') -PathType Leaf)) {
    throw 'Prepare and verify the isolated game copy before building.'
}
$statusFile = Join-Path $layout.Runtime 'preparation-status.json'
if (-not (Test-Path -LiteralPath $statusFile)) { throw 'The runtime preparation status is missing.' }
$prepared = Get-Content -LiteralPath $statusFile -Raw | ConvertFrom-Json
if ($prepared.Status -ne 'prepared_not_launched' -or -not $prepared.BackupStable -or -not $prepared.GameCopyVerified) {
    throw 'Runtime preparation is incomplete or failed. Review its preserved evidence before building.'
}
Assert-NoReparsePath -Path $layout.Feed
$taskHarmonyPackage = Join-Path $layout.Feed 'lib.harmony.2.3.3.nupkg'
if (-not (Test-Path -LiteralPath $taskHarmonyPackage -PathType Leaf)) {
    throw 'The reviewed local Lib.Harmony 2.3.3 package is missing. This script does not download dependencies.'
}
if ((Get-FileHash -LiteralPath $taskHarmonyPackage -Algorithm SHA256).Hash -ne '87B63DDB92F04FCB89C30B7EBAE473C948DD65E80569EB94AEF08B163BC3BF63') {
    throw 'The local Harmony package differs from the reviewed, pinned official download.'
}
$framework = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8'
if (-not (Test-Path -LiteralPath (Join-Path $framework 'mscorlib.dll') -PathType Leaf)) {
    throw '.NET Framework 4.8 reference assemblies are required; no reference-assembly package will be downloaded.'
}
$xnaRoot = Join-Path $env:WINDIR 'Microsoft.NET/assembly/GAC_32'
foreach ($assembly in @('Microsoft.Xna.Framework', 'Microsoft.Xna.Framework.Game', 'Microsoft.Xna.Framework.Graphics')) {
    $reference = Join-Path $xnaRoot "$assembly/v4.0_4.0.0.0__842cf8be1de50553/$assembly.dll"
    if (-not (Test-Path -LiteralPath $reference -PathType Leaf)) { throw "Required local XNA reference is missing: $reference" }
}
$dotnet = Get-Command dotnet -CommandType Application -ErrorAction Stop
$projects = @('AgentHost', 'AgentBridge', 'Controller')
foreach ($name in $projects) {
    $path = Join-Path $layout.Project "src/$name/$name.csproj"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Project is not yet implemented: $path" }
}

New-SafeDirectory -Path $layout.Packages -Root $layout.Runtime
New-SafeDirectory -Path $layout.Build -Root $layout.Runtime
New-SafeDirectory -Path (Join-Path $layout.Runtime 'logs') -Root $layout.Runtime
$configFile = Join-Path $layout.Runtime 'nuget.offline.config'
Assert-WithinRoot -Path $configFile -Root $layout.Runtime
Assert-NoReparsePath -Path $configFile
$escapedFeed = [Security.SecurityElement]::Escape($layout.Feed)
$escapedCache = [Security.SecurityElement]::Escape($layout.Packages)
$configText = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources><clear /><add key="reviewed-local-feed" value="$escapedFeed" /></packageSources>
  <packageSourceMapping><clear /><packageSource key="reviewed-local-feed"><package pattern="*" /></packageSource></packageSourceMapping>
  <config><add key="globalPackagesFolder" value="$escapedCache" /></config>
</configuration>
"@
[IO.File]::WriteAllText($configFile, $configText, (New-Object Text.UTF8Encoding($false)))
$attempt = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$logPath = Join-Path $layout.Runtime "logs/build-$attempt.log"
$resultPath = Join-Path $layout.Runtime "logs/build-$attempt.json"
$results = [ordered]@{ StartedUtc = [DateTime]::UtcNow.ToString('o'); Configuration = $Configuration; DecisionMode = 'rules'; GameStarted = $false; Results = @(); Status = 'building' }
$properties = @('-p:AutomaticallyUseReferenceAssemblyPackages=false', '-p:NuGetAudit=false',
    "-p:FrameworkPathOverride=$framework", "-p:TerrariaInstallPath=$($layout.Game)", "-p:XnaAssemblyRoot=$xnaRoot")

function Invoke-LoggedDotnet {
    param([string[]]$Arguments)
    & $dotnet.Source @Arguments 2>&1 | Tee-Object -FilePath $logPath -Append | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet exited with code $LASTEXITCODE; see $logPath" }
}

function Enable-OwnHostLargeAddressAware {
    # Only our fixed build output is editable. Terraria.exe and all DLLs remain
    # byte-for-byte unchanged. The original game's x86 PE already has this bit.
    $hostRoot = [IO.Path]::GetFullPath((Join-Path $layout.Build 'AgentHost'))
    $hostPath = [IO.Path]::GetFullPath((Join-Path $hostRoot 'TerrariaAgent.Host.exe'))
    Assert-WithinRoot -Path $hostRoot -Root $layout.Build
    Assert-WithinRoot -Path $hostPath -Root $hostRoot
    Assert-NoReparsePath -Path $hostPath
    if (-not (Test-Path -LiteralPath $hostPath -PathType Leaf)) { throw 'The own Host executable is missing after compilation.' }
    $stream = $null
    $reader = $null
    try {
        $stream = [IO.File]::Open($hostPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $reader = New-Object IO.BinaryReader($stream)
        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) { throw 'The own Host has an invalid MZ header.' }
        $stream.Position = 0x3C
        $peOffset = [long]$reader.ReadUInt32()
        if ($peOffset -lt 64 -or $peOffset + 26 -gt $stream.Length) { throw 'The own Host PE header offset is out of bounds.' }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0x014C) {
            throw 'The own Host must have a valid PE signature and x86 machine header.'
        }
        $stream.Position = $peOffset + 20
        $optionalSize = $reader.ReadUInt16()
        $characteristics = $reader.ReadUInt16()
        if ($optionalSize -lt 96 -or $peOffset + 24 + $optionalSize -gt $stream.Length -or
            $reader.ReadUInt16() -ne 0x010B -or ($characteristics -band 0x0002) -eq 0 -or
            ($characteristics -band 0x2000) -ne 0) {
            throw 'The own Host must be an executable PE32 image with a valid optional header, not a DLL.'
        }
        $expected = [uint16]($characteristics -bor 0x0020)
        if ($expected -ne $characteristics) {
            $flagBytes = [BitConverter]::GetBytes($expected)
            $stream.Position = $peOffset + 22
            $stream.Write($flagBytes, 0, 2)
            $stream.Flush($true)
        }
        $stream.Position = $peOffset + 22
        $actual = $reader.ReadUInt16()
        if ($actual -ne $expected -or ($actual -band 0x0020) -eq 0) {
            throw 'The own Host LargeAddressAware write could not be verified.'
        }
        [pscustomobject]@{ Machine = 'x86'; Format = 'PE32'; LargeAddressAware = $true;
            CharacteristicsBefore = ('0x{0:X4}' -f $characteristics); CharacteristicsAfter = ('0x{0:X4}' -f $actual) }
    } finally {
        if ($null -ne $reader) { $reader.Dispose() }
        if ($null -ne $stream) { $stream.Dispose() }
    }
}

try {
    foreach ($name in $projects) {
        Assert-NoGameRunning -RequireDesktopVisibility:$false
        $projectFile = Join-Path $layout.Project "src/$name/$name.csproj"
        Write-Output "Restoring $name from the reviewed local package feed only."
        Invoke-LoggedDotnet -Arguments (@('restore', $projectFile, '--configfile', $configFile, '--packages', $layout.Packages) + $properties)
        $destination = Join-Path $layout.Build $name
        New-SafeDirectory -Path $destination -Root $layout.Runtime
        Write-Output "Building $name without package restore or launching any game."
        Invoke-LoggedDotnet -Arguments (@('build', $projectFile, '--no-restore', '-c', $Configuration, '-o', $destination) + $properties)
        if ($name -eq 'AgentHost') {
            $results.HostPE = Enable-OwnHostLargeAddressAware
            Write-Output "Own Host PE32/x86 LargeAddressAware verified: $($results.HostPE.CharacteristicsBefore) -> $($results.HostPE.CharacteristicsAfter)."
        }
        if ($name -eq 'AgentHost' -and (Get-FileHash -LiteralPath (Join-Path $destination '0Harmony.dll') -Algorithm SHA256).Hash -ne '498EDE1D20A87AFAA6C7B1ED5B3BEB505B01DC418B952C1BE5C322204404B033') {
            throw 'The built Harmony DLL does not match the pinned package; inspect the local package cache.'
        }
        $results.Results += [pscustomobject]@{ Project = $name; Output = $destination; Compiled = $true; InGameVerified = $false }
    }
    $results.Status = 'compiled_not_game_verified'
    $results.CompletedUtc = [DateTime]::UtcNow.ToString('o')
    Write-WorkspaceJson -Value $results -Path $resultPath -AllowedRoot $layout.Runtime
    Write-Output 'Build completed. Compilation does not establish game loading, observation, input control, or save isolation.'
} catch {
    $results.Status = 'failed'
    $results.Error = $_.Exception.Message
    $results.CompletedUtc = [DateTime]::UtcNow.ToString('o')
    Write-WorkspaceJson -Value $results -Path $resultPath -AllowedRoot $layout.Runtime
    throw
}
