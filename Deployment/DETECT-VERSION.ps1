$required = [version]'3.3.1.0'
$exe = 'C:\Program Files\AT LiveOverlay\ATLiveOverlay.exe'
if (-not (Test-Path -LiteralPath $exe)) { Write-Output 'Not installed'; exit 1 }
try { $installed = [version](Get-Item -LiteralPath $exe).VersionInfo.FileVersion }
catch { Write-Output 'Version unreadable'; exit 1 }
if ($installed -ge $required) { Write-Output "Compliant: $installed"; exit 0 }
Write-Output "Outdated: $installed"; exit 1
