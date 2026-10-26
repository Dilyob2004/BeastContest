@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Start-BeastContest.ps1" -Docker -OpenBrowser
if errorlevel 1 (
  echo Startup failed. See the message above.
  pause
  exit /b 1
)

