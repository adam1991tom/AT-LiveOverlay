# AT LiveOverlay Companion Module v1.2.0

Development module for Bitfocus Companion. It connects to the AT LiveOverlay v4.1.1 HTTP API on TCP port 8765.

The API token config field uses Companion's `secret-text` input type, so it is masked in the configuration UI once saved. A `companion/manifest.json` is included as required by current Companion module builds. An invalid/missing token is reported distinctly (`Authentication failure`) rather than a generic connection error.

## What it exposes

- **Actions**: show/hide/reload/edit/live/lock/unlock/close per overlay, set URL, set opacity, set refresh interval, create overlay, show/hide/reload all, and load scene.
- **Feedbacks**: overlay visible, overlay locked, overlay exists, and scene active (by name) — the bundled presets already use these to reflect live state (e.g. the "Show overlay 1" button highlights green while it's visible).
- **Variables**: `version`, `build`, `overlay_count`, `active_scene`, plus per-overlay `overlay_<id>_name/url/visible/locked/opacity/refresh` that appear once that overlay exists.

## Setup

1. Install AT LiveOverlay v4.1.1 and open **Remote control** from its tray menu.
2. Click **Enable Windows Firewall access**.
3. Copy the **API token** shown in the Remote control window.
4. Install Node.js 18 or later on the Companion development machine.
5. Run `npm install` in this folder.
6. Add the folder as a local/development module in Companion.
7. Configure the presentation computer IP, port 8765, and the API token from step 3.

This is a source/development module and is not yet published in the official Companion module list.
