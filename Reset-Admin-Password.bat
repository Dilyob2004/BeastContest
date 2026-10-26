@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Reset-Admin-Password.ps1"
set "beastExitCode=%errorlevel%"
echo.
pause
exit /b %beastExitCode%
