@echo off
setlocal EnableExtensions
cd /d "%~dp0"
title AT LiveOverlay v4.0.3 MSI Builder

rem Run the reliable PowerShell build driver and always keep this window open.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Installer\Build-Installer.ps1"
set "RC=%ERRORLEVEL%"

echo.
if not "%RC%"=="0" (
  echo BUILD FAILED with error code %RC%.
  echo Open BUILD-LOGS\latest-build.log for the full error.
) else (
  echo BUILD COMPLETED SUCCESSFULLY.
)
echo.
pause
exit /b %RC%
