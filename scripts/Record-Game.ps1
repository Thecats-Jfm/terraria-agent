[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WindowTitle,
    [ValidateRange(10, 180)][int]$Seconds = 15,
    [switch]$DesktopAvailable
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $DesktopAvailable) { throw 'Recording requires the desktop to be free for game verification.' }
Import-Module (Join-Path $PSScriptRoot 'Workspace-Safety.psm1') -Force
$taskLayout = Get-WorkspaceLayout
$taskEvidence = Join-Path $taskLayout.Workspace 'outputs/terraria-evidence'
New-SafeDirectory -Path $taskEvidence -Root $taskLayout.Workspace
$taskClip = Join-Path $taskEvidence ('game-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8) + '.mp4')
$taskRecorder = Get-Command ffmpeg -CommandType Application -ErrorAction Stop
$taskProbe = Get-Command ffprobe -CommandType Application -ErrorAction Stop
# The title must come from an observed game window, not a guessed desktop handle.
# Capture this game window only; never silently fall back to the whole desktop.
& $taskRecorder.Source -hide_banner -loglevel warning -n -f gdigrab -framerate 30 -draw_mouse 1 `
    -i ("title=" + $WindowTitle) -t $Seconds -vf 'scale=min(1920\,iw):-2' `
    -c:v libx264 -preset veryfast -b:v 8M -maxrate 10M -bufsize 16M -pix_fmt yuv420p -an -movflags +faststart $taskClip
if ($LASTEXITCODE -ne 0) { throw 'Window recording failed; no screenshot fallback is labelled as video.' }
& $taskRecorder.Source -hide_banner -loglevel error -i $taskClip -f null -
if ($LASTEXITCODE -ne 0) { throw 'The recording could not be fully decoded.' }
$taskMetadataText = & $taskProbe.Source -v error -show_entries 'format=duration:stream=codec_name,width,height,r_frame_rate' -of json $taskClip
if ($LASTEXITCODE -ne 0) { throw 'The recording metadata could not be read.' }
$taskMetadata = ($taskMetadataText -join [Environment]::NewLine) | ConvertFrom-Json
$taskResult = [ordered]@{ Kind = 'continuous_video'; Path = $taskClip; WindowTitle = $WindowTitle; Silent = $true;
    EncodingSucceeded = $true; DecodeSucceeded = $true; PlaybackVerified = $false;
    Note = 'Encoding/decoding does not prove visible game content or playback. Open the clip in a player and verify before accepting it.'; Metadata = $taskMetadata }
Write-WorkspaceJson -Value $taskResult -Path ($taskClip + '.verification.json') -AllowedRoot $taskLayout.Workspace
Write-Output "Recorded and decoded: $taskClip"
Write-Output 'Player playback and visible game content still need visual verification.'
