@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-Direct-Test.ps1" -Mode stage-b -DesktopAvailable
pause
