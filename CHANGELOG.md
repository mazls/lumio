# Lumio Photobooth – Changelog 📜

Alle nennenswerten Änderungen und Versionen von Lumio werden in diesem Dokument festgehalten.

---

## [1.2.0] – 2026-10-17 – *Das große Hardware- & Stabilitäts-Update* 🚀

### 📷 In-Process DSLR / DSLM Tethering (dslrBooth-Engine)
- **Natives USB-Tethering ohne externe Software**:
  - Direkte PTP/WPD-Anbindung in Lumio eingebettet (keine zweite Software wie digiCamControl nötig, kein Nikon Webcam Utility erforderlich).
  - Volle Unterstützung für **Nikon (D5600, Z 50 II, D5xxx, D7xxx, Z-Serie)**, **Canon EOS**, **Sony Alpha**, **Fujifilm**, **Panasonic Lumix** und **Olympus**.
  - **Linux & Raspberry Pi**: Vollständige Multi-Vendor-Erkennung und Steuerung über **gphoto2** inklusive MJPEG-Live-View-Streaming mit ~25 FPS.
- **Zuverlässige Live-View & 24MP-Auslösung**:
  - Korrekte PTP/JPEG-Offset-Erkennung (`ImageDataPosition`) für glasklares, ruckelfreies Live-View auf Nikon-Kameras.
  - Nahtloser Spiegel- & Verschlusszyklus: Beim Auslösen pausiert der Live-View-Bus kurzzeitig, damit der mechanische Verschluss ungestört auslösen kann.
  - **Autofokus-Sicherheitsnetz**: Bei schlechten Lichtverhältnissen greift automatisch `CapturePhotoNoAf`, sodass die Fotobox zuverlässig auslöst und niemals blockiert.
- **📸 Nicht-speicherndes Testfoto**:
  - Neue Schaltfläche im Admin-Menü unter Kamera-Auswahl: Löst ein Testbild aus und zeigt eine temporäre Vollbildvorschau mit Auflösung, Dateigröße und Zeitstempel.
  - Das Bild wird **nicht** im Event-Ordner oder auf dem USB-Stick abgelegt und belastet nicht die Galerie.

### ⚙️ Modernes Touch-Admin-Menü & Performance
- **Sofortiges Öffnen ohne UI-Hänger**:
  - Windows Print Spooler RPC, USB-Laufwerksabfragen und WMI-Suchen wurden vollständig in den Hintergrund (`Task.Run`) verlagert und mit einem 60s-Cache abgesichert.
  - Pre-Warming beim App-Start: Drucker und Laufwerke werden bereits im Hintergrund initialisiert, während der Startbildschirm aktiv ist.
- **4 übersichtliche Tabs**:
  1. **⚙️ Allgemein**: Event-Name, Speicherort mit 1-Klick USB-Stick-Erkennung, Bildschirmtastatur-Schalter, Admin-PIN (Standard `1234`) und Schnellaktionen.
  2. **📷 Kamera**: Kamera-Auswahl, Live-View-Spiegelung, DSLR-Verbindungsstatus, Belichtungswerte (ISO, Blende, Verschlusszeit, EV) und Testfoto-Button.
  3. **🎬 Aufnahme**: Template-Auswahl (Einzelfoto, 4er Collage, Selphy-Streifen, GIF, Boomerang, Videobotschaften), Foto-Branding (Logo & Event-Text), Timer & Pausen.
  4. **🖨️ Drucker**: Canon Selphy CP1300/CP1500 Erkennung, Auto-Sofortdruck und Testdruck-Funktion.
- **⌨️ Integrierte Touch-Bildschirmtastatur**:
  - Klappt beim Tippen in Textfelder am unteren Bildschirmrand auf. Ideal für Microsoft Surface und Touch-Kioske ohne physische Tastatur.

### 💾 Speicher & Medien
- **USB-Stick Auto-Erkennung**: Angeschlossene USB-Sticks werden sofort erkannt und als Speicherziel vorgeschlagen.
- **Sicherer Fallback-Speicher**: Bei nicht beschreibbaren Pfaden weicht Lumio automatisch in den Bilder-Ordner aus.
- **Videobotschaften & Boomerang**: Gäste können persönliche Video-/Sprachnachrichten hinterlassen oder schnelle 1,3s Instagram-Boomerangs aufnehmen.
- **Verzögerungsfreier Starter (`start-lumio.ps1`)**: Beendet verwaiste Hintergrundprozesse vor dem Start automatisch, um DLL-Dateisperren zu verhindern.

---

## [1.1.0] – 2026-10-17 – *Multi-Modi & Cross-Plattform*

- **Aufnahme-Modi**: Einzelfoto, 4er Collage, Selphy-Doppelstreifen, animierte GIFs und Boomerang.
- **Linux PC Support**: Volle Lauffähigkeit auf CachyOS, Arch Linux, Ubuntu und Debian.
- **Raspberry Pi 4 / 5**: Eigenständige Builds für `linux-arm64` via Avalonia UI.
- **Canon Selphy Direktdruck**: Direktansteuerung des CP1300/CP1500 über Windows Spooler und CUPS.

---

## [1.0.0] – 2026-10-16 – *Initial Release*

- Erste Version der Lumio Photobooth-Software im Dark-Glass Touch-Design.
- Kiosk-Vollbildmodus mit PIN-Schutz.
- SkiaSharp 300-DPI Collagen-Renderer.
