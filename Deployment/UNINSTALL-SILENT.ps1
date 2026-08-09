$ErrorActionPreference = 'Stop'
$apps = @(
  Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue
  Get-ItemProperty 'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue
) | Where-Object { $_.DisplayName -eq 'AT LiveOverlay' }
if (-not $apps) { exit 0 }
foreach ($app in $apps) {
  if ($app.PSChildName -match '^\{[0-9A-Fa-f-]+\}$') {
    $p = Start-Process msiexec.exe -ArgumentList @('/x', $app.PSChildName, '/qn', '/norestart', '/L*v', "$env:ProgramData\AT LiveOverlay\Logs\uninstall.log") -Wait -PassThru
    if ($p.ExitCode -notin 0,1605,1614,3010) { exit $p.ExitCode }
  }
}
exit 0
