[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskProject = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
& dotnet run --project (Join-Path $taskProject 'tests/ProtocolChecks/ProtocolChecks.csproj')
exit $LASTEXITCODE
