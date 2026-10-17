<p align="center">
  <img src="assets/logo.png" width="180" alt="Lumio Logo" style="border-radius: 24px;" />
</p>

# Lumio 📸

Eine moderne, touch-optimierte Photobooth-Software für Windows & Linux. Gebaut für Partys, Hochzeiten und Events mit DSLR/DSLM-Kameras (**Nikon, Canon, Sony, Fuji**), Webcams und dem **Canon Selphy** Fotodrucker.

---

<p align="center">
  <img src="assets/screenshot_kiosk.jpg" width="85%" alt="Lumio Kiosk Modus" style="border-radius: 16px; box-shadow: 0 10px 30px rgba(0,0,0,0.5);" />
</p>

---

## Warum dieses Projekt?

Ich habe jahrelang **dslrBooth** für meine Fotobox genutzt und war eigentlich zufrieden. Bis die auf die glorreiche Idee kamen, ihr Lizenzmodell über den Haufen zu werfen – meine damals als "lebenslang" gekaufte Lizenz war auf einmal ungültig und man sollte ein teures Abo abschließen. 

Da hatte ich absolut keinen Bock drauf. Deshalb hab ich mich hingesetzt und eine eigene Software von Grund auf neu geschrieben: **Lumio**. 
Kein Abo, kein Lizenz-Wahnsinn, keine Internetpflicht auf der Feier und komplett auf Touch-Bedienung (insbesondere für mein Microsoft Surface und den Raspberry Pi) ausgelegt.

---

## Neue Features in Version 1.2.0 ✨

- 📷 **Natives DSLR & DSLM Tethering (wie dslrBooth)**:
  - Läuft direkt im Lumio-Prozess per USB PTP – **ohne externe Zusatzsoftware** und ohne Nikon Webcam Utility.
  - Volle Unterstützung für **Nikon (D5600, Z 50 II, D5xxx, Z-Serie)**, **Canon EOS**, **Sony Alpha**, **Fujifilm**, **Panasonic** und **Olympus**.
  - Glasklare Live-Vorschau mit automatischer Spiegel-Freigabe und Autofokus-Sicherheitsnetz (`CapturePhotoNoAf`).
  - **📸 Testfoto-Button**: Schnappschuss direkt im Admin-Menü zur Fokus- und Belichtungskontrolle (wird nicht gespeichert).
- 🗂️ **Touch-Admin-Menü mit 4 Tabs**:
  - **Allgemein**: Event-Name, Speicherordner, automatische USB-Stick-Erkennung, PIN-Schutz.
  - **Kamera**: Kamera-Auswahl, Live-View-Spiegelung, Verbindungsstatus und manuelle DSLR-Belichtungswerte.
  - **Aufnahme**: Templates, Foto-Branding (Logo & Event-Text), Timer, Zwischenbild-Pausen & Videobotschaften.
  - **Drucker**: Canon Selphy CP1300/CP1500 Anbindung, Auto-Sofortdruck und Testdruck-Button.
  - Sofortiges Öffnen ohne UI-Ruckler dank asynchronem Hardware-Laden und Drucker-Spooler-Caching.
- ⌨️ **Integrierte Bildschirmtastatur**:
  - Direkt im Admin-Menü für Touchscreens zuschaltbar.
  - Klappt beim Antippen von Textfeldern automatisch am unteren Bildschirmrand auf – kein physisches Keyboard erforderlich!
- 💾 **USB-Laufwerks-Erkennung**:
  - Erkennt eingesteckte USB-Sticks sofort im Admin-Menü. Ein Klick genügt, um alle Fotos & Videos des Abends direkt auf dem externen Stick zu sichern.

---

<p align="center">
  <img src="assets/screenshot_admin.jpg" width="85%" alt="Lumio Admin Dashboard mit Tabs & Bildschirmtastatur" style="border-radius: 16px; box-shadow: 0 10px 30px rgba(0,0,0,0.5);" />
</p>

---

## Alle Features im Überblick

- **📱 Touch-First Kiosk-Modus**: Vollbild-Oberfläche mit dunklem Glas-Design, großen Buttons und flüssigen Animationen.
- **📸 6 verschiedene Aufnahme-Modi**:
  - `Einzelfoto`: Schneller Einzelschuss (10x15 Postkarte)
  - `4er Collage`: 4 Aufnahmen im 2x2 Raster auf 10x15 cm
  - `Selphy Doppelstreifen`: 3 Aufnahmen nebeneinander verdoppelt als klassischer Fotostreifen (perfekt zum Zerschneiden)
  - `GIF`: 4 Posen mit Countdown, zusammengebaut als animierter 4-Bilder-Loop
  - `Boomerang`: Schneller 1,3s Action-Burst, der vor- und zurückspringt (Instagram-Style)
  - `Videobotschaft`: Persönliche Video- und Audio-Nachrichten der Gäste (Dauer im Admin-Menü einstellbar)
- **👀 Zwischenbild-Vorschau**: Bei Serienaufnahmen sieht man das gerade geschossene Bild kurz groß auf dem Bildschirm, bevor der nächste Countdown runterzählt.
- **⚙️ Volle DSLR-Kontrolle**: Verschlusszeit, Blende, ISO, Weißabgleich und Belichtungskorrektur manuell im Admin-Menü anpassbar.
- **🔒 PIN-Sperre für Gäste**: Gäste können nicht versehentlich in Windows/Linux landen. Admin-Bereich ist mit Zahlencode geschützt (Standard-PIN: `1234`). Notfall-Shortcut: `Esc` oder `Strg + A`.
- **🖨️ Canon Selphy Direktdruck**: Erkennt Canon Selphy Drucker (CP1300, CP1500, CP1200) automatisch und druckt randlose 10x15 Abzüge auf Knopfdruck oder per Auto-Print.
- **🖼️ Event-Galerie**: Kleines Vorschaubild unten in der Ecke; per Klick öffnet sich die Vollbild-Galerie zum Durchblättern aller Fotos des Abends inklusive Nachdruck-Funktion.
- **100% Offline**: Funktioniert im tiefsten Weinkeller oder im Festzelt ohne WLAN. Alle Fotos werden lokal geordnet nach Datum und Event gespeichert.

---

## Unterstützte Hardware & Plattformen

1. **Plattformen & Betriebssysteme**:
   - **Windows (x64)**: Normale PCs, Laptops & Touchscreen-All-in-Ones (Windows 10 & 11)
   - **Microsoft Surface (win-arm64)**: Native ARM64-Version für Surface Pro & Surface Laptop
   - **Linux PC (linux-x64)**: Arch Linux, CachyOS, Ubuntu, Debian, Fedora etc.
   - **Raspberry Pi (linux-arm64)**: Raspberry Pi 4 & Raspberry Pi 5 (64-Bit Raspberry Pi OS / Debian)
2. **Kameras**:
   - **DSLR & Spiegellose Kameras**: Nikon, Canon, Sony, Fujifilm, Panasonic Lumix, Olympus
     - *Windows*: Über digiCamControl (kostenlos, MIT-Lizenz) oder direkte PTP-Erkennung
     - *Linux*: Nativ über gphoto2
   - **Webcams**: USB-Webcams (Logitech, Anker etc.) sowie integrierte Surface-Front-/Rückkameras
   - **Simulierte Test-Kamera**: Integrierte virtuelle Testkamera mit Live-Zeitstempel zum Testen ohne Hardware
3. **Drucker**:
   - Canon Selphy CP1300 / CP1500 / CP1200 / CP1000 über den Windows-Druckertreiber oder CUPS auf Linux/Raspberry Pi

---

## Schnellstart

### Fertige Version herunterladen (ohne Installation)
Einfach unter **[Releases](https://github.com/mazls/lumio/releases)** die passende ZIP-Datei laden:
- **`Lumio-win-x64.zip`** für normale Windows-Laptops und PCs
- **`Lumio-win-arm64.zip`** für Microsoft Surface mit ARM/Snapdragon-Prozessor
- **`Lumio-linux-x64.zip`** für **Linux PCs** (CachyOS, Arch Linux, Ubuntu, Debian, Fedora etc.)
- **`Lumio-linux-arm64.zip`** für **Raspberry Pi 4 / 5** (64-Bit OS)

Es muss keine .NET Runtime vorinstalliert sein, da alle Releases komplett eigenständig gebündelt (self-contained) sind.

#### Auf Linux (CachyOS, Arch, Ubuntu oder Raspberry Pi) starten:
```bash
# Auf Arch Linux / CachyOS / Manjaro:
sudo pacman -S gphoto2 cups ffmpeg
sudo systemctl enable --now cups

# Oder auf Debian / Ubuntu / Raspberry Pi OS:
sudo apt update && sudo apt install -y gphoto2 cups ffmpeg

# Release entpacken und starten
unzip Lumio-linux-x64.zip -d lumio   # bzw. Lumio-linux-arm64.zip auf dem Pi
cd lumio
chmod +x run-lumio.sh Lumio
./run-lumio.sh
```

---

## Bedienung

1. **Start**: Software starten (`Lumio.exe` / `Lumio.bat` unter Windows, `./run-lumio.sh` unter Linux).
2. **Admin-Menü öffnen**: Oben rechts auf das Schloss-Symbol tippen (oder Tastatur: `Esc` bzw. `Strg+A`) und die PIN eingeben (Standard: `1234`).
3. **Einrichten**:
   - **Allgemein**: Event-Name eintragen (z. B. "Hochzeit Sarah & Tom") und USB-Speicherpfad wählen. Bei Bedarf Touch-Bildschirmtastatur aktivieren.
   - **Kamera**: Kamera auswählen (DSLR, Webcam oder Simulation) und Belichtungswerte anpassen.
   - **Aufnahme**: Vorlage und Branding (Logo/Event-Text) festlegen.
   - **Drucker**: Drucker auswählen und ggf. Testdruck senden.
4. **Party-Modus**: Box für die Gäste bereitstellen – die Gäste tippen einfach auf ihren Wunschmodus und lächeln in die Kamera!

---

## Für Entwickler

```powershell
# Repo klonen
git clone https://github.com/mazls/lumio.git
cd lumio

# Tests ausführen
dotnet test Lumio.slnx

# Standalone lokal bauen (start-lumio.ps1 erkennt automatisch x64 vs arm64)
powershell -ExecutionPolicy Bypass -File .\start-lumio.ps1 -Rebuild
```

### Projektstruktur
- `src/Lumio.App`: WPF-Oberfläche für Windows (Touch-Kiosk, Themes, Dialoge)
- `src/Lumio.App.Avalonia`: Cross-Plattform Avalonia UI für Linux & Raspberry Pi
- `src/Lumio.Core`: Session-Ablauf, State Machine, Einstellungen & Speicherlogik
- `src/Lumio.Camera`: Multi-Vendor DSLR-Treiber (Nikon, Canon, Sony via digiCamControl & gphoto2), Webcams & Testkamera
- `src/Lumio.Imaging`: SkiaSharp 300-DPI Collagen-Renderer & GIF/Boomerang/Video-Encoder
- `src/Lumio.Printing`: Canon Selphy Spooler & CUPS-Integration
- `tests/Lumio.Tests`: 28 automatisierte Unit-Tests für Core, Templates, Collagen, GIF & DSLR

---

## Lizenz & Drittanbieter-Bibliotheken 📜

Lumio steht unter der **[Lumio Fair-Use & Event License](LICENSE)**:

- ✅ **Gewerbliche Event-Nutzung ERLAUBT:** Du darfst die kompilierte Software völlig frei auf privaten Partys sowie gewerblichen Veranstaltungen (z. B. Fotobox-Vermietung, Hochzeiten, Firmenfeiern, Messen) einsetzen und damit Geld verdienen. Es gibt keine versteckten Kosten, keine Lizenzgebühren und kein Abo.
- ❌ **Quellcode Nicht-Kommerziell:** Der Quellcode ist einsehbar und darf für private Zwecke, zum Lernen und für Beiträge/Verbesserungen (Pull Requests) genutzt werden. Die kommerzielle Verwertung des Quellcodes (z. B. Weiterverkauf des Quellcodes, Erstellen kommerzieller Closed-Source-Produkte oder Bezahl-Apps) ist ohne vorherige schriftliche Genehmigung untersagt.

### Verwendete Bibliotheken & Lizenzen:
- **SkiaSharp** (Google Skia Wrapper) – MIT Lizenz
- **Avalonia UI** – MIT Lizenz
- **XamlAnimatedGif** – Apache-2.0 Lizenz
- **digiCamControl** (Kamera-Brücke) – MIT Lizenz (Copyright 2014 Duka Istvan)
- **gphoto2 & CUPS** – Auf Linux als externe System-Tools aufgerufen (GPL/LGPL/Apache)
- **Outfit Font** – SIL Open Font License 1.1

Das vollständige Änderungsprotokoll aller Versionen findest du im **[CHANGELOG.md](CHANGELOG.md)**.

Viel Spaß beim Feiern und Fotos schießen! 🍻
