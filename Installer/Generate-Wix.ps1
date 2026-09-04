param(
  [Parameter(Mandatory=$true)][string]$PublishDir,
  [Parameter(Mandatory=$true)][string]$OutputFile
)
$ErrorActionPreference = 'Stop'
function Esc([string]$s) { return [System.Security.SecurityElement]::Escape($s) }
function SafeId([string]$prefix,[string]$value) {
  $bytes = [Text.Encoding]::UTF8.GetBytes($value.ToLowerInvariant())
  $sha = [Security.Cryptography.SHA1]::Create()
  try { $hash = ($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') }) -join '' }
  finally { $sha.Dispose() }
  return $prefix + $hash.Substring(0,20)
}
function Rel([string]$base,[string]$full) {
  $b = [IO.Path]::GetFullPath($base).TrimEnd('\') + '\'
  $f = [IO.Path]::GetFullPath($full)
  if (-not $f.StartsWith($b,[StringComparison]::OrdinalIgnoreCase)) { throw "Path is outside publish directory: $full" }
  return $f.Substring($b.Length)
}
$files = Get-ChildItem -LiteralPath $PublishDir -File -Recurse | Sort-Object FullName
$dirs = @{}; $dirs[''] = 'INSTALLFOLDER'
foreach($file in $files) {
  $rel = Rel $PublishDir $file.FullName
  $dir = [IO.Path]::GetDirectoryName($rel)
  while($dir -and -not $dirs.ContainsKey($dir)) {
    $dirs[$dir] = SafeId 'D_' $dir
    $parent = [IO.Path]::GetDirectoryName($dir)
    if($null -eq $parent){$parent=''}
    $dir = $parent
  }
}
$sb = New-Object Text.StringBuilder
[void]$sb.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
[void]$sb.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs" xmlns:ui="http://wixtoolset.org/schemas/v4/wxs/ui" xmlns:firewall="http://wixtoolset.org/schemas/v4/wxs/firewall">')
[void]$sb.AppendLine('  <Package Name="AT LiveOverlay" Manufacturer="AT" Version="4.0.0" UpgradeCode="F4D7C037-96CC-4EF5-B75A-5438F62FD8F7" Scope="perMachine" InstallerVersion="500">')
[void]$sb.AppendLine('    <Property Id="ARPPRODUCTICON" Value="AppIcon" />')
[void]$sb.AppendLine('    <Property Id="ARPCOMMENTS" Value="Professional always-on-top webpage overlays with Bitfocus Companion control." />')
[void]$sb.AppendLine('    <ui:WixUI Id="WixUI_InstallDir" InstallDirectory="INSTALLFOLDER" />')
[void]$sb.AppendLine('    <WixVariable Id="WixUIBannerBmp" Value="'+(Esc (Join-Path $PSScriptRoot 'Assets\banner.bmp'))+'" />')
[void]$sb.AppendLine('    <WixVariable Id="WixUIDialogBmp" Value="'+(Esc (Join-Path $PSScriptRoot 'Assets\dialog.bmp'))+'" />')
[void]$sb.AppendLine('    <WixVariable Id="WixUILicenseRtf" Value="'+(Esc (Join-Path $PSScriptRoot 'License.rtf'))+'" />')

[void]$sb.AppendLine('    <MajorUpgrade DowngradeErrorMessage="A newer version of AT LiveOverlay is already installed." Schedule="afterInstallValidate" MigrateFeatures="yes" />')
[void]$sb.AppendLine('    <MediaTemplate EmbedCab="yes" CompressionLevel="medium" />')
[void]$sb.AppendLine('    <Property Id="WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT" Value="Launch AT LiveOverlay" />')
[void]$sb.AppendLine('    <!-- Stable upgrade handling: one immediate close action only. -->')
[void]$sb.AppendLine('    <!-- Deferred taskkill and cleanup actions were removed because they caused MSI error 2762. -->')
[void]$sb.AppendLine('    <CustomAction Id="CloseATLiveOverlay" Directory="SystemFolder" ExeCommand="cmd.exe /D /C taskkill /F /T /IM ATLiveOverlay.exe &gt;nul 2&gt;&amp;1" Execute="immediate" Impersonate="yes" Return="ignore" />')
[void]$sb.AppendLine('    <InstallExecuteSequence>')
[void]$sb.AppendLine('      <Custom Action="CloseATLiveOverlay" Before="InstallValidate" Condition="WIX_UPGRADE_DETECTED OR REMOVE=&quot;ALL&quot;" />')
[void]$sb.AppendLine('    </InstallExecuteSequence>')
[void]$sb.AppendLine('    <Feature Id="MainFeature" Title="AT LiveOverlay" Level="1"><ComponentGroupRef Id="ProductComponents" /></Feature>')
[void]$sb.AppendLine('  </Package>')
[void]$sb.AppendLine('  <Fragment>')
[void]$sb.AppendLine('    <StandardDirectory Id="ProgramFiles64Folder">')
[void]$sb.AppendLine('      <Directory Id="INSTALLFOLDER" Name="AT LiveOverlay">')
# emit nested dirs recursively
function EmitDirs([string]$parentRel,[int]$indent) {
  $children = $dirs.Keys | Where-Object { $_ -ne '' -and ([IO.Path]::GetDirectoryName($_) -replace '^$','') -eq $parentRel } | Sort-Object
  foreach($child in $children){
    $name=[IO.Path]::GetFileName($child); $id=$dirs[$child]; $spaces=' ' * $indent
    [void]$sb.AppendLine($spaces + '<Directory Id="' + $id + '" Name="' + (Esc $name) + '">')
    EmitDirs $child ($indent+2)
    [void]$sb.AppendLine($spaces + '</Directory>')
  }
}
EmitDirs '' 8
[void]$sb.AppendLine('      </Directory>')
[void]$sb.AppendLine('    </StandardDirectory>')
[void]$sb.AppendLine('    <StandardDirectory Id="DesktopFolder" />')
[void]$sb.AppendLine('    <StandardDirectory Id="ProgramMenuFolder"><Directory Id="AppMenuFolder" Name="AT LiveOverlay" /></StandardDirectory>')
[void]$sb.AppendLine('  </Fragment>')
[void]$sb.AppendLine('  <Fragment>')
[void]$sb.AppendLine('    <ComponentGroup Id="ProductComponents">')
foreach($file in $files){
  $rel=Rel $PublishDir $file.FullName; $dir=[IO.Path]::GetDirectoryName($rel); if($null -eq $dir){$dir=''}
  $dirId=$dirs[$dir]; $comp=SafeId 'C_' $rel; $fid=SafeId 'F_' $rel
  [void]$sb.AppendLine('      <Component Id="'+$comp+'" Directory="'+$dirId+'" Guid="*">')
  if($rel -ieq 'ATLiveOverlay.exe'){
    [void]$sb.AppendLine('        <File Id="'+$fid+'" Source="'+(Esc $file.FullName)+'" KeyPath="yes">')
    [void]$sb.AppendLine('          <firewall:FirewallException Id="CompanionFirewallRule" Name="AT LiveOverlay Companion" Description="Allows Bitfocus Companion to control AT LiveOverlay on TCP port 8765." Port="8765" Protocol="tcp" Profile="private" Scope="localSubnet" IgnoreFailure="no" />')
    [void]$sb.AppendLine('        </File>')
    [void]$sb.AppendLine('        <Shortcut Id="DesktopShortcut" Directory="DesktopFolder" Name="AT LiveOverlay" WorkingDirectory="INSTALLFOLDER" Target="[#'+$fid+']" Icon="AppIcon" IconIndex="0" />')
    [void]$sb.AppendLine('        <Shortcut Id="StartMenuShortcut" Directory="AppMenuFolder" Name="AT LiveOverlay" WorkingDirectory="INSTALLFOLDER" Target="[#'+$fid+']" Icon="AppIcon" IconIndex="0" />')
  } else {
    [void]$sb.AppendLine('        <File Id="'+$fid+'" Source="'+(Esc $file.FullName)+'" KeyPath="yes" />')
  }
  [void]$sb.AppendLine('      </Component>')
}
[void]$sb.AppendLine('      <Component Id="MenuFolderComponent" Directory="AppMenuFolder" Guid="*"><RemoveFolder Id="RemoveAppMenuFolder" On="uninstall" /><RegistryValue Root="HKCU" Key="Software\AT\AT LiveOverlay" Name="installed" Type="integer" Value="1" KeyPath="yes" /></Component>')
[void]$sb.AppendLine('      <Component Id="InstallFolderCleanup" Directory="INSTALLFOLDER" Guid="*"><RemoveFolder Id="RemoveInstallFolder" On="uninstall" /><RegistryValue Root="HKLM" Key="Software\AT\AT LiveOverlay" Name="InstallFolderCleanup" Type="integer" Value="1" KeyPath="yes" /></Component>')
[void]$sb.AppendLine('    </ComponentGroup>')
[void]$sb.AppendLine('  </Fragment>')
[void]$sb.AppendLine('  <Fragment><Icon Id="AppIcon" SourceFile="'+(Esc (Join-Path $PublishDir 'app.ico'))+'" /></Fragment>')
[void]$sb.AppendLine('</Wix>')
[IO.File]::WriteAllText($OutputFile,$sb.ToString(),(New-Object Text.UTF8Encoding($false)))
Write-Host "Generated $OutputFile using $($files.Count) files."
