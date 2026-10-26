@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Create-Admin.ps1"
set "beastExitCode=%errorlevel%"
echo.
pause
exit /b %beastExitCode%

