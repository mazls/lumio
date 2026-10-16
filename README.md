<p align="center">
  <img src="assets/logo.png" width="200" alt="Lumio Logo" style="border-radius: 24px;" />
</p>

# Lumio 📸

Eine moderne, touch-optimierte Photobooth-Software für Windows. Gebaut für Partys, Hochzeiten und Events mit Nikon DSLRs/Spiegellosen Kameras und dem Canon Selphy CP1300 Fotodrucker.

---

## Warum dieses Projekt?

Ich habe jahrelang **dslrBooth** für meine Fotobox genutzt und war eigentlich zufrieden. Bis die auf die glorreiche Idee kamen, ihr Lizenzmodell über den Haufen zu werfen – meine damals als "lebenslang" gekaufte Lizenz war auf einmal ungültig und man sollte ein teures Abo abschliessen. 

Da hatte ich absolut keinen Bock drauf. Deshalb hab ich mich hingesetzt und eine eigene Software von Grund auf neu geschrieben: **Lumio**. 
Kein Abo, kein Lizenz-Wahnsinn, keine Internetpflicht auf der Feier und komplett auf Touch-Bedienung (insbesondere für mein Microsoft Surface) ausgelegt.

---

## Features

- **📱 Touch-First Kiosk-Modus**: Vollbild-Oberfläche mit dunklem Glas-Design, großen Buttons und flüssigen Animationen.
- **📸 5 verschiedene Aufnahme-Modi**:
  - `Einzelfoto`: Schneller Einzelschuss (10x15 Postkarte)
  - `4er Collage`: 4 Aufnahmen im 2x2 Raster auf 10x15 cm
  - `Selphy Doppelstreifen`: 3 Aufnahmen nebeneinander verdoppelt als klassischer Fotostreifen (perfekt zum Zerschneiden)
  - `GIF`: 4 Posen mit Countdown, zusammengebaut als 4-Bilder-Loop
  - `Boomerang`: Schneller 1,3s Action-Burst der vor- und zurückspringt (Instagram-Style)
- **👀 Zwischenbild-Vorschau**: Bei Serienaufnahmen sieht man das gerade geschossene Bild kurz gross auf dem Bildschirm, bevor der nächste Countdown runterzählt (Pausenzeit im Admin-Menü einstellbar).
- **⚙️ Volle DSLR-Kontrolle**: Wie bei dslrBooth können Verschlusszeit, Blende, ISO, Weißabgleich und Belichtungskorrektur manuell im Admin-Menü eingestellt werden.
- **🔒 PIN-Sperre für Gäste**: Gäste können nicht versehentlich in Windows landen. Admin-Bereich ist mit 4-stelligem Zahlencode geschützt (Standard-PIN: `1234`). Notfall-Shortcut: `Esc` oder `Strg + A`.
- **🖨️ Canon Selphy CP1300 Direktdruck**: Erkennt den Selphy-Drucker automatisch und druckt randlose 10x15 Abzüge auf Knopfdruck oder per Auto-Print direkt nach dem Schuss.
- **🖼️ Event-Galerie**: Kleines Vorschaubild unten in der Ecke; per Klick öffnet sich die Vollbild-Galerie zum Durchblättern aller Fotos des Abends inklusive Nachdruck-Funktion.
- **100% Offline**: Funktioniert im tiefsten Keller oder im Festzelt ohne WLAN. Alle Fotos werden lokal nach Datum und Event geordnet gespeichert.

---

## Unterstützte Hardware & Plattformen

1. **Plattformen & Betriebssysteme**:
   - **Windows (x64)**: Normale PCs, Laptops & Touchscreen-All-in-Ones (Windows 10 & 11)
   - **Microsoft Surface (win-arm64)**: Native ARM64-Version für Surface Pro & Surface Laptop
   - **Raspberry Pi (linux-arm64)**: Getestet auf Raspberry Pi 4 & Raspberry Pi 5 (64-Bit Raspberry Pi OS / Debian)
2. **Kameras**:
   - **Nikon DSLR & Z-Serie**: Getestet mit Nikon D5600 und Nikon Z 50 II per USB (Windows PTP & Linux `gphoto2`)
   - **Webcams & Pi Cams**: USB-Webcams, integrierte Surface-Kameras sowie Raspberry Pi Camera Modules
   - **Simulierte Test-Kamera**: Integrierte virtuelle Testkamera mit Live-Zeitstempel zum Testen ohne Hardware
3. **Drucker**:
   - Canon Selphy CP1300 (sowie CP1500 / CP1200 / CP1000) über den Windows-Druckertreiber oder CUPS auf Linux/Raspberry Pi

---

## Schnellstart

### Fertige Version herunterladen (ohne Installation)
Einfach unter **[Releases](https://github.com/mazls/lumio/releases)** die passende ZIP-Datei laden:
- **`Lumio-win-x64.zip`** für normale Windows-Laptops und PCs
- **`Lumio-win-arm64.zip`** für Microsoft Surface mit ARM/Snapdragon-Prozessor
- **`Lumio-linux-arm64.zip`** für **Raspberry Pi 4 / 5** (64-Bit OS)

Es muss keine .NET Runtime installiert werden, da alle Releases komplett eigenständig gebündelt sind.

#### Auf dem Raspberry Pi starten:
```bash
# Empfohlene Pakete fuer Nikon & Selphy-Druck installieren
sudo apt update && sudo apt install -y gphoto2 cups

# Release entpacken und starten
unzip Lumio-linux-arm64.zip -d lumio
cd lumio
chmod +x run-lumio.sh Lumio
./run-lumio.sh
```

---

## Bedienung

1. **Start**: Software starten (`Lumio.exe` oder per Doppelklick auf `Lumio.bat`).
2. **Admin-Menü öffnen**: Oben rechts auf das Schloss-Symbol tippen (oder Tastatur: `Esc` bzw. `Strg+A`) und die PIN eingeben (Standard: `1234`).
3. **Einrichten**:
   - Event-Namen vergeben (z. B. "Hochzeit Sarah & Tom")
   - Kamera auswählen (Nikon, Webcam oder Testkamera)
   - Bei Bedarf Belichtungswerte (ISO, Verschlusszeit, Blende) anpassen
   - Countdown (z. B. 3s) und Review-Dauer (z. B. 12s) einstellen
   - Drucker auswählen und ggf. Testdruck machen
4. **Party-Modus**: Box für die Gäste hinstellen – die Gäste tippen einfach auf ihren Wunschmodus und lächeln in die Kamera!

---

## Automatische Builds (GitHub Actions CI/CD)

Im Repository ist ein automatisierter GitHub Actions Workflow (`.github/workflows/build-release.yml`) hinterlegt:
- **Bei jedem Commit / Push auf `main`**: Der Code wird gebaut, alle Unit-Tests werden ausgeführt und die fertigen Standalone-Pakete (`Lumio-win-x64.zip` und `Lumio-win-arm64.zip`) werden automatisch im Workflow-Run und unter dem Tag `latest` bereitgestellt.
- **Bei Git-Tags (z. B. `git tag v1.0.0 && git push --tags`)**: Erstellt automatisch ein offizielles GitHub-Release mit Release-Notes und den beiden Download-ZIPs.

---

## Für Entwickler

Wer selbst am Code basteln möchte:

```powershell
# Repo klonen
git clone https://github.com/mazls/lumio.git
cd lumio

# Tests ausführen
dotnet test Lumio.slnx

# Standalone lokal bauen
dotnet publish src/Lumio.App/Lumio.App.csproj -c Release -r win-arm64 --self-contained true -o ./publish
```

### Projektstruktur
- `src/Lumio.App`: WPF-Oberfläche, Touch-Controls, Kiosk-Modus und Dialoge
- `src/Lumio.Core`: Session-Ablauf, State Machine, Einstellungen & Speicherlogik
- `src/Lumio.Camera`: Kamera-Treiber (Nikon USB-PTP, Windows MediaCapture, Testkamera)
- `src/Lumio.Imaging`: SkiaSharp 300-DPI Collagen-Renderer & GIF/Boomerang Encoder
- `src/Lumio.Printing`: Canon Selphy Spooler-Integration
- `tests/Lumio.Tests`: Unit-Tests für Core, Templates, Collagen und GIF-Encoding

---

## Geplante Features (Roadmap)

- [ ] Leerlauf-Slideshow (Bildschirmschoner mit den schönsten Fotos des Abends)
- [ ] QR-Code aufs Handy zum direkten Download per lokalem Mini-Webserver
- [ ] Greenscreen / Chroma-Keying
- [ ] Eigener visueller Template-Designer

---

## Lizenz & Gewerbliche Nutzung 📜

Lumio steht unter der **[Lumio Fair-Use & Event License](LICENSE.md)**:

- ✅ **Gewerbliche Event-Nutzung ERLAUBT:** Du darfst die kompilierte Software völlig frei auf privaten Partys sowie gewerblichen Veranstaltungen (z. B. Fotobox-Vermietung, Hochzeiten, Firmenfeiern, Messen) einsetzen und damit Geld verdienen. Es gibt keine versteckten Kosten, keine Lizenzgebühren und kein Abo.
- ❌ **Quellcode Nicht-Kommerziell:** Der Quellcode ist einsehbar und darf für private Zwecke, zum Lernen und für Beiträge/Verbesserungen (Pull Requests) genutzt werden. Die kommerzielle Verwertung des Quellcodes (z. B. Weiterverkauf des Quellcodes, Erstellen kommerzieller Closed-Source-Produkte oder Bezahl-Apps) ist ohne vorherige schriftliche Genehmigung untersagt.
- ℹ️ **Info-Button in der App:** Über das kleine Info-Symbol (ℹ️) oben rechts in der App können die Lizenz und Projekt-Details jederzeit direkt vor Ort eingesehen werden.

Feedback, Fehler oder Feature-Wünsche gerne einfach als [Issue](https://github.com/mazls/lumio/issues) melden!

Viel Spass beim Feiern und Fotos schiessen! 🍻
