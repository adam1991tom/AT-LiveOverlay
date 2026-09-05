@echo off
setlocal
set "MSI=%~dp0AT-LiveOverlay-v4.0.1-Setup.msi"
set "LOGDIR=%ProgramData%\AT LiveOverlay\Logs"
if not exist "%LOGDIR%" mkdir "%LOGDIR%"
msiexec.exe /i "%MSI%" /qn /norestart /L*v "%LOGDIR%\install-4.0.1.log"
exit /b %ERRORLEVEL%
