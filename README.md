<p align="center">
  <a href="README.de.md">🇩🇪 Deutsch</a> &nbsp;|&nbsp; <a href="README.md"><b>🇬🇧 English</b></a>
</p>

<p align="center">
  <img src="assets/logo.png" width="180" alt="Lumio Logo" style="border-radius: 24px;" />
</p>

# Lumio 📸

A modern, touch-optimized photobooth software for Windows & Linux. Built for parties, weddings, and events with DSLR/mirrorless cameras (**Nikon, Canon, Sony, Fuji**), webcams, and the **Canon Selphy** photo printer.

---

<p align="center">
  <img src="assets/screenshot_kiosk.jpg" width="85%" alt="Lumio Kiosk Mode" style="border-radius: 16px; box-shadow: 0 10px 30px rgba(0,0,0,0.5);" />
</p>

---

## Why this project?

I used **dslrBooth** for my photo booth for years and was generally satisfied. Until they decided to overturn their licensing model – my license, which was originally bought as a "lifetime" license, suddenly became invalid and they demanded an expensive recurring subscription.

I was completely done with that. So I sat down and built my own photobooth software from scratch: **Lumio**. 
No subscriptions, no licensing headaches, no internet required at events, and fully designed for touch operation (especially for my Microsoft Surface and Raspberry Pi).

---

## New Features in Version 1.2.1 ✨

- 📷 **Native DSLR & Mirrorless Tethering (like dslrBooth)**:
  - Runs directly within the Lumio process via USB PTP – **no external background tools** and no Nikon Webcam Utility required.
  - Full support for **Nikon (D5600, Z 50 II, D5xxx, Z-series)**, **Canon EOS**, **Sony Alpha**, **Fujifilm**, **Panasonic**, and **Olympus**.
  - Crystal-clear live preview with automatic mirror-flip and autofocus safety fallback (`CapturePhotoNoAf`).
  - **📸 Test Photo Button**: Take a quick snapshot directly in the Admin menu for focus and exposure checks (not saved to the gallery).
- 🗂️ **Touch Admin Dashboard with 4 Tabs**:
  - **General**: Event name, storage location, automatic USB drive detection, PIN protection.
  - **Camera**: Camera selector, live view mirroring, connection status, and manual DSLR exposure values.
  - **Capture**: Templates, photo branding (logo & event text overlay), countdown timers, review pauses & video guestbook messages.
  - **Printer**: Canon Selphy CP1300/CP1500 integration, auto instant print, and test print button.
  - Instant opening without UI stutter thanks to asynchronous hardware polling and printer spooler caching.
- ⌨️ **Integrated On-Screen Keyboard**:
  - Can be toggled on directly in the Admin menu for touchscreens.
  - Automatically slides up from the bottom when tapping text fields – no physical keyboard required!
- 💾 **USB Flash Drive Detection**:
  - Automatically detects plugged-in USB thumb drives in the Admin menu. One click is all it takes to backup all event photos & videos directly to the external drive.

---

<p align="center">
  <img src="assets/screenshot_admin.jpg" width="85%" alt="Lumio Admin Dashboard with Tabs & On-Screen Keyboard" style="border-radius: 16px; box-shadow: 0 10px 30px rgba(0,0,0,0.5);" />
</p>

---

## Feature Overview

- **📱 Touch-First Kiosk Mode**: Fullscreen UI with dark glassmorphism design, oversized touch targets, and smooth animations.
- **📸 6 Different Capture Modes**:
  - `Single Photo`: Fast single shot (4x6" / 10x15cm postcard format)
  - `4-Photo Collage`: 4 shots in a 2x2 grid on a 4x6" print
  - `Selphy Double Strip`: 3 poses duplicated side-by-side as a classic photo strip (ready to cut in half)
  - `GIF`: 4 poses with countdown, compiled into an animated 4-frame looping GIF
  - `Boomerang`: Rapid 1.3-second action burst looping forward and backward (Instagram style)
  - `Video Message`: Personal video and audio guestbook messages from guests (configurable duration in Admin)
- **👀 Intermediate Photo Review**: In multi-shot modes, guests see a quick full-screen preview of their last pose before the next countdown begins.
- **⚙️ Full DSLR Exposure Control**: Manually adjust shutter speed, aperture, ISO, white balance, and exposure compensation from the Admin menu.
- **🔒 Guest PIN Lockout**: Prevents guests from accidentally accessing Windows/Linux. The Admin area is protected by a PIN code (default: `1234`). Emergency shortcuts: `Esc` or `Ctrl + A`.
- **🖨️ Canon Selphy Direct Printing**: Auto-detects Canon Selphy printers (CP1300, CP1500, CP1200) and prints borderless 4x6" photos on demand or automatically after capture.
- **🖼️ Event Gallery**: Bottom-corner live thumbnail preview; tap to open a fullscreen gallery to browse all photos of the night with a reprint function.
- **100% Offline**: Works in basements, barns, or festival tents without Wi-Fi. All media is saved locally and organized by date and event name.

---

## Supported Hardware & Platforms

1. **Platforms & Operating Systems**:
   - **Windows (x64)**: Regular PCs, laptops & touchscreen all-in-ones (Windows 10 & 11)
   - **Microsoft Surface (win-arm64)**: Native ARM64 build for Surface Pro & Surface Laptop
   - **Linux PC (linux-x64)**: Arch Linux, CachyOS, Ubuntu, Debian, Fedora, etc.
   - **Raspberry Pi (linux-arm64)**: Raspberry Pi 4 & Raspberry Pi 5 (64-bit Raspberry Pi OS / Debian)
2. **Cameras**:
   - **DSLR & Mirrorless Cameras**: Nikon, Canon, Sony, Fujifilm, Panasonic Lumix, Olympus
     - *Windows*: Via digiCamControl (free, MIT license) or direct PTP detection
     - *Linux*: Native via gphoto2
   - **Webcams**: USB webcams (Logitech, Anker, etc.) and integrated Surface front/rear cameras
   - **Simulated Test Camera**: Built-in virtual camera with live timestamp for testing without hardware
3. **Printers**:
   - Canon Selphy CP1300 / CP1500 / CP1200 / CP1000 via Windows printer driver or CUPS on Linux/Raspberry Pi

---

## Quick Start

### Download Pre-built Release (No installation required)
Simply grab the appropriate ZIP archive under **[Releases](https://github.com/mazls/lumio/releases)**:
- **`Lumio-win-x64.zip`** for standard Windows laptops and PCs
- **`Lumio-win-arm64.zip`** for Microsoft Surface with ARM/Snapdragon processors
- **`Lumio-linux-x64.zip`** for **Linux PCs** (CachyOS, Arch Linux, Ubuntu, Debian, Fedora, etc.)
- **`Lumio-linux-arm64.zip`** for **Raspberry Pi 4 / 5** (64-bit OS)

No .NET runtime needs to be preinstalled, as all releases are fully self-contained.

#### Running on Linux (CachyOS, Arch, Ubuntu, or Raspberry Pi):
```bash
# On Arch Linux / CachyOS / Manjaro:
sudo pacman -S gphoto2 cups ffmpeg
sudo systemctl enable --now cups

# Or on Debian / Ubuntu / Raspberry Pi OS:
sudo apt update && sudo apt install -y gphoto2 cups ffmpeg

# Unpack release and run
unzip Lumio-linux-x64.zip -d lumio   # or Lumio-linux-arm64.zip on the Pi
cd lumio
chmod +x run-lumio.sh Lumio
./run-lumio.sh
```

---

## How to Use

1. **Start**: Launch the application (`Lumio.exe` / `Lumio.bat` on Windows, `./run-lumio.sh` on Linux).
2. **Open Admin Menu**: Tap the lock icon in the top-right corner (or keyboard: `Esc` / `Ctrl+A`) and enter your PIN (default: `1234`).
3. **Configure**:
   - **General**: Enter event name (e.g. "Wedding Sarah & Tom") and select the storage path. Enable the on-screen keyboard if needed.
   - **Camera**: Select camera (DSLR, webcam, or simulation) and adjust exposure settings.
   - **Capture**: Choose template and set up photo branding (logo / event text overlay).
   - **Printer**: Select printer and send a test print if desired.
4. **Party Mode**: Hand the booth over to your guests – they simply tap their desired mode on the touchscreen and strike a pose!

---

## For Developers

```powershell
# Clone repo
git clone https://github.com/mazls/lumio.git
cd lumio

# Run tests
dotnet test Lumio.slnx

# Build standalone locally (start-lumio.ps1 auto-detects x64 vs arm64)
powershell -ExecutionPolicy Bypass -File .\start-lumio.ps1 -Rebuild
```

### Project Structure
- `src/Lumio.App`: WPF UI for Windows (touch kiosk, themes, dialogs)
- `src/Lumio.App.Avalonia`: Cross-platform Avalonia UI for Linux & Raspberry Pi
- `src/Lumio.Core`: Session flow, state machine, settings & storage logic
- `src/Lumio.Camera`: Multi-vendor DSLR drivers (Nikon, Canon, Sony via digiCamControl & gphoto2), webcams & test camera
- `src/Lumio.Imaging`: SkiaSharp 300-DPI collage renderer & GIF/Boomerang/video encoders
- `src/Lumio.Printing`: Canon Selphy spooler & CUPS integration
- `tests/Lumio.Tests`: 28 automated unit tests for Core, templates, collages, GIF & DSLR

---

## License & Third-Party Libraries 📜

Lumio is distributed under the **[Lumio Fair-Use & Event License](LICENSE)**:

- ✅ **Commercial Event Use ALLOWED:** You are completely free to use the compiled software at private parties and commercial events (e.g., photo booth rentals, weddings, corporate events, trade shows) and earn money with it. There are no hidden fees, licensing costs, or subscriptions.
- ❌ **Source Code Non-Commercial:** The source code is visible and may be used for personal purposes, learning, and contributions/improvements (Pull Requests). Commercial exploitation of the source code (e.g., reselling source code, creating commercial closed-source products or paid apps) is prohibited without prior written permission.

### Used Libraries & Licenses:
- **SkiaSharp** (Google Skia Wrapper) – MIT License
- **Avalonia UI** – MIT License
- **XamlAnimatedGif** – Apache-2.0 License
- **digiCamControl** (Camera Bridge) – MIT License (Copyright 2014 Duka Istvan)
- **gphoto2 & CUPS** – Invoked on Linux as external system utilities (GPL/LGPL/Apache)
- **Outfit Font** – SIL Open Font License 1.1

Check **[CHANGELOG.md](CHANGELOG.md)** for full release notes and version history.

Have fun partying and taking photos! 🍻
