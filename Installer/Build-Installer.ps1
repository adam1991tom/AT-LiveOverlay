$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$Root = Split-Path -Parent $PSScriptRoot
$LogDir = Join-Path $Root 'BUILD-LOGS'
$LogFile = Join-Path $LogDir 'latest-build.log'
$PublishDir = Join-Path $Root 'publish'
$FinalDir = Join-Path $Root 'FINAL-INSTALLER'
$ToolsDir = Join-Path $Root '.tools'
$WixExe = Join-Path $ToolsDir 'wix.exe'
$Project = Join-Path $Root 'ATLiveOverlay\ATLiveOverlay.csproj'
$NugetConfig = Join-Path $Root 'NuGet.Config'
$MsiPath = Join-Path $FinalDir 'AT-LiveOverlay-v4.0.0-Setup.msi'

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
if (Test-Path $LogFile) { Remove-Item $LogFile -Force }
Start-Transcript -Path $LogFile -Force | Out-Null

function Write-Step([string]$Text) {
    Write-Host "`n$Text" -ForegroundColor Cyan
}

function Invoke-Native {
    param(
        [Parameter(Mandatory=$true)][string]$FilePath,
        [Parameter(ValueFromRemainingArguments=$true)][string[]]$Arguments
    )
    Write-Host ('> "' + $FilePath + '" ' + ($Arguments -join ' ')) -ForegroundColor DarkGray
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE`: $FilePath $($Arguments -join ' ')"
    }
}

try {
    Clear-Host
    Write-Host '============================================================'
    Write-Host '         AT LIVEOVERLAY v4.0.0 - FINAL MSI BUILDER'
    Write-Host '============================================================'
    Write-Host "Build log: $LogFile"

    Write-Step '[1/6] Locating .NET 8 SDK...'
    $dotnetCandidates = New-Object System.Collections.Generic.List[string]

    $dotnetCommand = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($dotnetCommand -and $dotnetCommand.Source) {
        $dotnetCandidates.Add($dotnetCommand.Source)
    }

    if ($env:ProgramFiles) {
        $dotnetCandidates.Add((Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'))
    }

    $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    if ($programFilesX86) {
        $dotnetCandidates.Add((Join-Path $programFilesX86 'dotnet\dotnet.exe'))
    }

    $Dotnet = $dotnetCandidates |
        Where-Object { $_ -and (Test-Path $_) } |
        Select-Object -Unique |
        Select-Object -First 1

    function Has8Sdk([string]$DotnetPath) {
        if (-not $DotnetPath -or -not (Test-Path $DotnetPath)) { return $false }
        $sdks = & $DotnetPath --list-sdks 2>$null
        return ($LASTEXITCODE -eq 0) -and ($null -ne ($sdks | Select-String '^8\.'))
    }

    if (-not (Has8Sdk $Dotnet)) {
        Write-Host '.NET 8 SDK was not found. Installing with Winget...' -ForegroundColor Yellow
        $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
        if (-not $winget) { throw 'Winget is unavailable. Install the .NET 8 SDK manually, then run this builder again.' }
        Invoke-Native $winget.Source 'install' '--id' 'Microsoft.DotNet.SDK.8' '--exact' '--source' 'winget' '--accept-package-agreements' '--accept-source-agreements'
        $Dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    }
    if (-not (Has8Sdk $Dotnet)) { throw 'The .NET 8 SDK is not installed. A runtime alone is not enough.' }
    Write-Host "Found: $Dotnet" -ForegroundColor Green

    Write-Step '[2/6] Generating version and build metadata...'
    & powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Generate-BuildInfo.ps1') -OutputFile (Join-Path $Root 'ATLiveOverlay\BuildInfo.g.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Build metadata generation failed.' }

    Write-Step '[2/6] Restoring application packages...'
    Invoke-Native $Dotnet 'restore' $Project '--configfile' $NugetConfig

    Write-Step '[3/6] Publishing the self-contained application...'
    if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
    Invoke-Native $Dotnet 'publish' $Project '-c' 'Release' '-r' 'win-x64' '--self-contained' 'true' '-o' $PublishDir '--no-restore'
    $AppExe = Join-Path $PublishDir 'ATLiveOverlay.exe'
    if (-not (Test-Path $AppExe)) { throw 'Publish completed without creating ATLiveOverlay.exe.' }
    Copy-Item (Join-Path $Root 'ATLiveOverlay\app.ico') (Join-Path $PublishDir 'app.ico') -Force
    Copy-Item (Join-Path $Root 'ATLiveOverlay\app.png') (Join-Path $PublishDir 'app.png') -Force

    Write-Step '[3/6] Downloading the WebView2 Runtime installers...'
    & powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Download-WebView2.ps1') -OutputDir $PublishDir
    if ($LASTEXITCODE -ne 0) { throw 'WebView2 Runtime download failed.' }
    $bootstrapper = Join-Path $PublishDir 'MicrosoftEdgeWebView2Setup.exe'
    if ((Test-Path $bootstrapper) -and (Get-Item $bootstrapper).Length -lt 100000) {
        throw 'The Microsoft WebView2 bootstrapper download was incomplete.'
    }

    Write-Step '[4/6] Preparing WiX 4.0.6...'
    New-Item -ItemType Directory -Force -Path $ToolsDir | Out-Null
    if (-not (Test-Path $WixExe)) {
        Invoke-Native $Dotnet 'tool' 'install' 'wix' '--tool-path' $ToolsDir '--version' '4.0.6'
    }
    if (-not (Test-Path $WixExe)) { throw 'WiX could not be installed or located.' }

    # Ignore the return code when already installed; the actual MSI build validates it.
    & $WixExe extension add 'WixToolset.UI.wixext/4.0.6'
    & $WixExe extension add 'WixToolset.Firewall.wixext/4.0.6'
    & $WixExe extension add 'WixToolset.Util.wixext/4.0.6'

    Write-Step '[5/6] Generating the MSI package definition...'
    $Wxs = Join-Path $PSScriptRoot 'ATLiveOverlay.wxs'
    & powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Generate-Wix.ps1') -PublishDir $PublishDir -OutputFile $Wxs
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $Wxs)) { throw 'The WiX package definition was not generated.' }

    Write-Step '[6/6] Building the one-file MSI installer...'
    if (Test-Path $FinalDir) { Remove-Item $FinalDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $FinalDir | Out-Null
    Invoke-Native $WixExe 'build' $Wxs '-arch' 'x64' '-ext' 'WixToolset.UI.wixext' '-ext' 'WixToolset.Firewall.wixext' '-ext' 'WixToolset.Util.wixext' '-o' $MsiPath
    if (-not (Test-Path $MsiPath)) { throw 'WiX completed without creating the MSI.' }

    Write-Step '[6/6] Creating deployment metadata and checksum...'
    $Hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $MsiPath).Hash.ToLowerInvariant()
    Set-Content -LiteralPath ($MsiPath + '.sha256') -Value ($Hash + '  ' + [IO.Path]::GetFileName($MsiPath)) -Encoding ASCII
    $ReleaseInfo = [ordered]@{
        product = 'AT LiveOverlay'
        version = '4.0.0'
        installer = [IO.Path]::GetFileName($MsiPath)
        sha256 = $Hash
        silentInstall = 'msiexec.exe /i "' + [IO.Path]::GetFileName($MsiPath) + '" /qn /norestart /L*v "%ProgramData%\AT LiveOverlay\Logs\install-4.0.0.log"'
        detectionFile = 'C:\Program Files\AT LiveOverlay\ATLiveOverlay.exe'
        minimumFileVersion = '4.0.0.0'
        companionPort = 8765
        firewallProfile = 'Private'
    }
    $ReleaseInfo | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $FinalDir 'release-info.json') -Encoding UTF8
    Copy-Item -Path (Join-Path $Root 'Deployment\*') -Destination $FinalDir -Recurse -Force

    Write-Host "`n============================================================" -ForegroundColor Green
    Write-Host 'BUILD COMPLETE' -ForegroundColor Green
    Write-Host '============================================================' -ForegroundColor Green
    Write-Host "Installer created at:`n$MsiPath" -ForegroundColor White
    Stop-Transcript | Out-Null
    Start-Process explorer.exe -ArgumentList ('/select,"' + $MsiPath + '"')
    exit 0
}
catch {
    Write-Host "`n============================================================" -ForegroundColor Red
    Write-Host 'BUILD FAILED' -ForegroundColor Red
    Write-Host '============================================================' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host "`nFull log: $LogFile" -ForegroundColor Yellow
    try { Stop-Transcript | Out-Null } catch {}
    exit 1
}
