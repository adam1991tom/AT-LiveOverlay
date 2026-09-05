AT LiveOverlay v4.0.3 Enterprise Builder

1. Extract this entire ZIP to a normal local folder.
2. Double-click BUILD-FINAL-INSTALLER.cmd.
3. Leave the internet connected while NuGet, WiX and WebView2 files are prepared.
4. Find the completed deployment package in FINAL-INSTALLER.

The output includes:
- AT-LiveOverlay-v4.0.3-Setup.msi
- SHA-256 checksum
- release-info.json
- Action1 / Intune / GPO / PDQ silent deployment scripts

The MSI automatically adds the AT LiveOverlay Companion firewall rule for TCP 8765 on Private networks and removes it on uninstall. It uses the existing stable UpgradeCode and WiX MajorUpgrade for silent in-place upgrades.
