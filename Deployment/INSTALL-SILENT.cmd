@echo off
setlocal
set "MSI=%~dp0AT-LiveOverlay-v4.1.0-Setup.msi"
set "LOGDIR=%ProgramData%\AT LiveOverlay\Logs"
if not exist "%LOGDIR%" mkdir "%LOGDIR%"
msiexec.exe /i "%MSI%" /qn /norestart /L*v "%LOGDIR%\install-4.1.0.log"
exit /b %ERRORLEVEL%
