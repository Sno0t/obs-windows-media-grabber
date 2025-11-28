# 🎵 MediaInfoGrabber

A lightweight Windows application that extracts media information from Windows Media Session and creates a customizable web overlay for streaming and recording applications.

## 📖 What It Does

MediaInfoGrabber monitors Windows Media Session (SMTC - System Media Transport Controls) to capture currently playing media information and generates web-based overlay files that can be used in streaming software, OBS alternatives, or any application that supports web overlays.

## ⚡ Quick Start

1. Download and run `MediaInfoGrabber.exe`
2. Overlay files are created in the same folder
3. Add `overlay.html` as Browser Source in OBS/Streamlabs
4. Widget updates automatically - done! 🎉

For portable mode (no files): `MediaInfoGrabber.exe --portable` → Open `http://localhost:8080/overlay.html`

## ✨ Features

- **🎧 Real-time Media Monitoring**: Automatically detects media from Spotify, YouTube, VLC, and other SMTC-compatible applications
- **🖼️ Album Cover Extraction**: Downloads and caches album artwork when available
- **🎨 Customizable Web Overlay**: Fully scalable and themeable overlay with CSS variables
- **📦 Multiple Output Modes**: 
  - Separate files mode (default): Creates `overlay.html`, `overlay.css`, `overlay.js`
  - Compact mode: Creates single `overlay.html` with embedded CSS/JS
  - **NEW**: Portable mode with internal web server (zero file writes)
- **📱 Responsive Design**: Scales perfectly from small widgets to full-screen displays
- **🎬 Animation Support**: Fly-in/out animations, fade effects, auto-hide timers
- **🏷️ Title Bar (NEW)**: Optional configurable title bar with custom text and alignment
- **📲 QR Code Display (NEW)**: Switchable QR code overlay with smooth fade transitions
- **🎼 Metadata Enrichment**: Optional MusicBrainz integration for additional track information

## 🎵 Supported Applications

**Tested and confirmed working:**
- ✅ Spotify (Desktop)

**Likely to work (SMTC-compatible desktop apps):**
- VLC Media Player
- Apple Music (Desktop)
- Windows Media Player
- Groove Music
- Foobar2000
- AIMP

**Known limitations:**
- ❌ YouTube (Browser) - Browsers typically don't expose SMTC properly
- ❌ Web players in general - Limited or no SMTC support
- ⚠️ Some apps may not provide album artwork through SMTC

*Note: This app relies on Windows Media Session (SMTC). If media controls don't appear in Windows 10/11 media overlay, the app won't be detected.*

## 🚀 Usage

### Basic Usage
```bash
MediaInfoGrabber.exe
```
Creates separate files: `overlay.html`, `overlay.css`, `overlay.js`, `cover.jpg`, `data.json`

### Compact Mode
```bash
MediaInfoGrabber.exe --compact
```
Creates single file: `overlay.html` (with embedded CSS/JS)

### 🌐 Portable Mode (NEW)
```bash
MediaInfoGrabber.exe --portable [--port 8080] [--app spotify]
```
Runs an internal web server instead of creating files on disk. Perfect for:
- ⚡ Temporary usage without file system writes
- 🌍 Network access from multiple devices
- 🔒 Clean environments where file creation is restricted

**Default URL**: `http://localhost:8080/overlay.html`
**Custom Port**: Use `--port XXXX` to specify a different port
**App Filter**: Use `--app APPNAME` to only monitor specific apps (bypasses whitelist file)

**Available endpoints in portable mode:**
- `/` or `/overlay.html` - Main overlay page
- `/overlay.css` - CSS file (non-compact mode only)
- `/overlay.js` - JavaScript file (non-compact mode only)  
- `/nowplaying.json` - Real-time media data
- `/cover.jpg` - Current album artwork
- `/usage.md` - Documentation

**Examples:**
```bash
# Basic portable mode
MediaInfoGrabber.exe --portable

# Custom port
MediaInfoGrabber.exe --portable --port 9999

# Only monitor Spotify
MediaInfoGrabber.exe --portable --port 8124 --app spotify

# Only monitor Apple Music
MediaInfoGrabber.exe --portable --app applemusic
```

### Output Files

**Standard Mode:**
- `overlay.html` - Main overlay page
- `overlay.css` - Styling and configuration
- `overlay.js` - Logic and animations  
- `cover.jpg` - Current album artwork (when available)
- `data.json` - Real-time media data
- `usage.md` - Detailed customization guide

**Compact Mode:**
- `overlay.html` - Self-contained overlay with embedded CSS/JS
- `cover.jpg` - Current album artwork (when available)
- `data.json` - Real-time media data
- `usage.md` - Detailed customization guide

## 🔌 Integration

### Streaming Software
1. Add a **Browser Source** 
2. Point to the generated `overlay.html` file
3. Set dimensions (recommended: 1600x400 for default scaling)
4. Enable "Refresh browser when scene becomes active" if needed

### Web Browsers
Simply open `overlay.html` in any modern web browser for testing and preview.

## 🎨 Customization

The overlay is highly customizable through CSS variables. See `usage.md` for detailed configuration options including:

- 📐 Cover size and scaling
- 🎨 Colors and themes  
- 🎬 Animations and effects
- 🔤 Typography and layout
- ⏱️ Auto-hide and repeat timers
- **🏷️ Title bar text and alignment** (NEW)
- **📲 QR code URL, text, and intervals** (NEW)

## 📋 Requirements

- Windows 10/11
- .NET 8.0 Runtime
- Media applications that support Windows Media Session (Spotify, YouTube, VLC, etc.)

## 🔧 Technical Details

- **Language**: C# (.NET 8.0)
- **Media Source**: Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager
- **Output Format**: HTML5 + CSS3 + ES6 JavaScript
- **Cover Art**: Extracted directly from media session thumbnail
- **Data Format**: JSON with real-time updates every 500ms
- **QR Generation**: Client-side using davidshimjs/qrcodejs (MIT licensed)

## 💝 Special Thanks

- **ChatGPT** - For assistance with documentation and explaining concepts
- **Codeium** - For helping understand and document Windows SMTC (System Media Transport Controls) and other technical implementations
- **davidshimjs** - For the excellent MIT-licensed QRCode.js library
