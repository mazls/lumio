#!/usr/bin/env bash
set -e
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
cd "$DIR"

# Ensure execution permissions
chmod +x "$DIR/Lumio"

# Check for gphoto2 (recommended for Nikon DSLRs)
if ! command -v gphoto2 &> /dev/null; then
    echo "Hinweis: 'gphoto2' wurde nicht gefunden. Fuer Nikon DSLRs bitte installieren:"
    echo "  - Arch / CachyOS / Manjaro:   sudo pacman -S gphoto2"
    echo "  - Debian / Ubuntu / Pi OS:    sudo apt install -y gphoto2"
fi

# Check for CUPS (recommended for Canon Selphy)
if ! command -v lp &> /dev/null; then
    echo "Hinweis: 'cups' wurde nicht gefunden. Fuer Canon Selphy Drucke bitte installieren:"
    echo "  - Arch / CachyOS / Manjaro:   sudo pacman -S cups && sudo systemctl enable --now cups"
    echo "  - Debian / Ubuntu / Pi OS:    sudo apt install -y cups"
# Check for ffmpeg (recommended for USB Webcams like Anker / Logitech)
if ! command -v ffmpeg &> /dev/null; then
    echo "Hinweis: 'ffmpeg' wurde nicht gefunden. Fuer USB-Webcams (z. B. Anker) bitte installieren:"
    echo "  - Arch / CachyOS / Manjaro:   sudo pacman -S ffmpeg"
    echo "  - Debian / Ubuntu / Pi OS:    sudo apt install -y ffmpeg"
fi

echo "Starte Lumio Photobooth..."
./Lumio "$@"
