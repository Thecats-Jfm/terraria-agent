@echo off
setlocal EnableExtensions DisableDelayedExpansion
rem Do not inherit PowerShell 7 modules into Windows PowerShell 5.1.
rem This setting is local to this launcher and its child process.
set "PSModulePath=%SystemRoot%\System32\WindowsPowerShell\v1.0\Modules"
title Terraria Agent - Recover health
echo Reuse your existing isolated game window in its paused state.
echo This starts a 60-second rule-based recovery trial and waits up to 60 seconds for human permission.
echo After starting this window, focus Terraria, press Ctrl+Shift+Insert once, and release all keys.
echo Controls require that fresh human permission. No keyboard simulation or reconnect retry is performed.
echo Use Ctrl+Shift+Backspace to stop, or Ctrl+Shift+Home to request normal pause.
echo.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run-Controller.ps1" -Mode recover-health -Seconds 60 -Arm -PermissionWaitSeconds 60
set "taskExitCode=%errorlevel%"
echo.
echo Controller script exit code: %taskExitCode%
echo Check the printed result and confirm the game is paused before leaving it unattended.
pause
exit /b %taskExitCode%
