@echo off
setlocal EnableExtensions DisableDelayedExpansion
rem Do not inherit PowerShell 7 modules into Windows PowerShell 5.1.
rem This setting is local to this launcher and its child process.
set "PSModulePath=%SystemRoot%\System32\WindowsPowerShell\v1.0\Modules"
title Terraria Agent - Main challenge
echo Starting the isolated main challenge with stage B/C observations enabled.
echo Select your isolated Classic character and world.
echo If the game is already running, reuse its paused window. This launcher refuses duplicate runs.
echo Keep the desktop available while opening the game.
echo.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-Game.ps1" -DesktopAvailable -EnableStageB
set "taskExitCode=%errorlevel%"
echo.
echo Launch script exit code: %taskExitCode%
echo Read any error above before continuing. This window stays open for review.
pause
exit /b %taskExitCode%
