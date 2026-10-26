@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Create-User.ps1"
set "beastExitCode=%errorlevel%"
echo.
pause
exit /b %beastExitCode%
