param(
  [Parameter(Mandatory=$true)][string]$OutputFile
)
$ErrorActionPreference = 'Stop'
$stamp = Get-Date -Format 'yyyyMMdd.HHmm'
$built = Get-Date -Format 'dd MMMM yyyy HH:mm'
$content = @(
  'namespace ATLiveOverlay;'
  'internal static class BuildInfo'
  '{'
  '    public const string Version = "3.3.1";'
  ('    public const string Build = "' + $stamp + '";')
  ('    public const string BuiltOn = "' + $built + '";')
  '}'
)
[IO.File]::WriteAllLines($OutputFile, $content, (New-Object Text.UTF8Encoding($false)))
Write-Host "Generated build metadata: Version 3.3.1, Build $stamp"
