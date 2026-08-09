@echo off
net session >nul 2>&1 || (powershell -NoProfile -Command "Start-Process '%~f0' -Verb RunAs" & exit /b)
netsh http add urlacl url=http://+:8765/ user=Everyone >nul 2>&1
netsh advfirewall firewall delete rule name="AT LiveOverlay Companion" >nul 2>&1
netsh advfirewall firewall add rule name="AT LiveOverlay Companion" dir=in action=allow protocol=TCP localport=8765 profile=private
pause
