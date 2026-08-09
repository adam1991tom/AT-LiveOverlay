param([Parameter(Mandatory=$true)][string]$OutputDir)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$bootstrapper = Join-Path $OutputDir 'MicrosoftEdgeWebView2Setup.exe'
$standalone = Join-Path $OutputDir 'MicrosoftEdgeWebView2RuntimeInstallerX64.exe'

Write-Host '  Downloading Microsoft Evergreen bootstrapper...'
Invoke-WebRequest -UseBasicParsing -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $bootstrapper

# Microsoft publishes the current standalone URL on the official WebView2 download page.
# Attempt to package the full offline x64 runtime as well. The app falls back to the
# bootstrapper if Microsoft changes the page markup.
try {
    Write-Host '  Locating Microsoft Evergreen Standalone x64 installer...'
    $page = Invoke-WebRequest -UseBasicParsing -Uri 'https://developer.microsoft.com/en-us/microsoft-edge/webview2/'
    $html = [Net.WebUtility]::HtmlDecode($page.Content)
    $matches = [regex]::Matches($html, 'https:[^"''<> ]+MicrosoftEdgeWebView2RuntimeInstallerX64\.exe[^"''<> ]*', 'IgnoreCase')
    $url = $matches | ForEach-Object { $_.Value.Replace('&amp;','&') } | Select-Object -First 1
    if ($url) {
        Write-Host '  Downloading Microsoft Evergreen Standalone x64 installer...'
        Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $standalone
    } else {
        Write-Warning 'Standalone URL was not exposed by the Microsoft page. The official bootstrapper will be bundled instead.'
    }
} catch {
    Write-Warning ('Standalone download was unavailable: ' + $_.Exception.Message)
}

if (-not (Test-Path $bootstrapper) -and -not (Test-Path $standalone)) {
    throw 'No Microsoft WebView2 Runtime installer could be downloaded.'
}
