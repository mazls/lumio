# Lumio Photobooth – Changelog 📜

Alle nennenswerten Änderungen und Versionen von Lumio werden in diesem Dokument festgehalten.

---

## [1.2.0] – 2026-10-17

### 🚀 Neu & Highlights
- **Multi-Vendor DSLR & DSLM Unterstützung**:
  - Unterstützung für alle großen Kamerahersteller (**Nikon, Canon, Sony, Fujifilm, Panasonic Lumix, Olympus**) per USB.
  - **Windows**: Anbindung an die bewährte Open-Source-Brücke **digiCamControl** (lokale HTTP-API) mit Live-View-Streaming, Fernauslösung und voller Belichtungssteuerung (ISO, Blende, Verschlusszeit, EV).
  - **Linux & Raspberry Pi**: Vollständige Multi-Vendor-Erkennung und Steuerung über **gphoto2** inklusive MJPEG-Live-View-Streaming mit ~25 FPS.
  - Automatische Abwärtskompatibilität: Bisherige Einstellungen (`CameraBackend.Nikon`) werden nahtlos und verlustfrei auf das neue universelle `Dslr`-Backend migriert.
- **Modernes, touch-optimiertes Admin-Menü mit 4 Tabs**:
  - Übersichtliche Aufteilung in:
    1. **⚙️ Allgemein**: Event-Name, Speicherort mit USB-Laufwerk-Erkennung & Dateibrowser, Bildschirmtastatur-Schalter, Admin-PIN und Schnellaktionen.
    2. **📷 Kamera**: Kamera-Auswahl, Live-View-Spiegelung, digiCamControl-Statusprüfung und manuelle DSLR-Belichtungswerte.
    3. **🎬 Aufnahme**: Template-Auswahl, Logo-Branding, Event-Text-Formatierung, Timer, Zwischenbild-Pausen und Videobotschaften.
    4. **🖨️ Drucker**: Canon Selphy CP1300/CP1500 Erkennung, Auto-Sofortdruck und Testdruck-Funktion.
  - Verfügbar in **beiden** Versionen: Windows (WPF) und Linux/Raspberry Pi (Avalonia).
- **Integrierte Touch-Bildschirmtastatur**:
  - In den allgemeinen Einstellungen per Checkbox ein- und ausschaltbar.
  - Klappt beim Tippen in Textfelder (Event-Name, Pfad, PIN) automatisch am unteren Bildschirmrand auf.
  - Ermöglicht vollwertige Kiosk-Bedienung auf Touchscreens ohne physische Tastatur (Windows Surface, Raspberry Pi Touch Display, All-in-One PCs).

### 🛠️ Verbesserungen & Wartung
- **Lizenzbereinigung**: Redundante `LICENSE.md` bereinigt, offizielle `LICENSE` (LFEL) beibehalten und Drittanbieter-Lizenzen (SkiaSharp, Avalonia, digiCamControl etc.) auf vollständige Lizenzkonformität geprüft.
- **Skripte**:
  - `start-lumio.ps1`: Automatische Erkennung der Host-Architektur (`win-arm64` für Surface Pro vs. `win-x64` für Intel/AMD-Laptops).
  - `run-lumio.sh`: Syntaxfehler (`fi`) behoben und Hilfetexte für DSLRs erweitert.
- **Unit Tests**: Testabdeckung für DSLR-Optionen, Kamera-Werte-Mapping, MJPEG-Stream-Splitter und Settings-Migration auf 28 Tests erweitert.

---

## [1.1.8] – 2026-10-17
- **USB-Stick Auto-Erkennung**: Erkennt angeschlossene USB-Sticks und externe Festplatten automatisch im Admin-Menü. Fotos & Videos können mit einem Klick direkt extern gesichert werden.
- **Ordner-Durchsuchen Picker**: Eigener Ordner-Auswahldialog für den Speicherpfad.
- **Sicherer Fallback-Speicher**: Bei nicht erreichbarem Pfad wird sicher in den Benutzer-Bilderordner ausgewichen.

## [1.1.7] – 2026-10-17
- **Videobotschaften-Modus**: Gäste können persönliche Videobotschaften für das Brautpaar oder den Gastgeber aufnehmen (inkl. Live-Audio und MP4/WebM-Speicherung).
- Maximale Videodauer im Admin-Menü einstellbar (5–120 Sekunden).
- Startbildschirm-Button für Videobotschaften optional deaktiverbar.

## [1.1.6] – 2026-10-17
- **Foto-Logo Branding**: Lumio-Logo kann in den vier Ecken der Fotos ein- oder ausgeblendet werden.
- **Event-Text Formatierung**: Flexible Darstellung (z. B. "Event – 2026", "Event • 17.10.2026" oder "Nur Event-Name").
- High-Contrast Dropdowns für beste Lesbarkeit auf Touchscreens.
- Auto-Rebuild in `start-lumio.ps1` bei geänderten Quelldateien.

## [1.1.5] – 2026-10-17
- **Kamera-Fix Windows**: Problem mit schwarzem Live-View bei bestimmten Webcams und Surface-Kameras behoben (`WritePixels` Lock und `Direct3DSurface` Fallback).

## [1.1.4] – 2026-10-17
- **Linux Admin Dashboard**: Vollständige Kamera- und Belichtungssteuerung in der Avalonia-Oberfläche für Linux & Raspberry Pi ergänzt.

## [1.1.3] – 2026-10-16
- Live-View Mirror Transform in Avalonia über `RenderTransform` optimiert.

## [1.1.2] – 2026-10-16
- Global Color Table im GIF-Header ergänzt und Wiedergabe über native Player abgesichert.

## [1.1.1] – 2026-10-16
- Surface Frontkamera Initialisierung gefixt (SharedMode und FrameReader Fallback).

## [1.1.0] – 2026-10-16
- Support für **Linux x64** (CachyOS, Arch Linux, Ubuntu PCs) inklusive CI-Release.
- Logo in `.ico`, Taskleiste und Header integriert.
- LZW-Komprimierung für animierte GIFs optimiert.

## [1.0.0] – 2026-10-16
- **Initial Release**: Lumio Photobooth
- Kiosk-Vollbildmodus im Dark-Glass-Design.
- 5 Modi: Einzelfoto, 4er Collage, Selphy Doppelstreifen, GIF und Boomerang.
- Nikon DSLR-Steuerung per USB (D5600, Z 50 II).
- Canon Selphy CP1300 Direktdruck via Windows Spooler und Linux CUPS.
- 4-stellige PIN-Sperre für das Admin-Menü.
- Raspberry Pi (linux-arm64) Unterstützung via Avalonia UI.
