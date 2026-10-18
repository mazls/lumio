<p align="center">
  <a href="README.de.md"><b>🇩🇪 Deutsch</b></a> &nbsp;|&nbsp; <a href="README.md">🇬🇧 English</a>
</p>

<p align="center">
  <img src="assets/logo.png" width="180" alt="Lumio Logo" style="border-radius: 24px;" />
</p>

# Lumio 📸

Eine moderne, touch-optimierte Photobooth-Software für Windows, Linux & Android. Gebaut für Partys, Hochzeiten und Events mit DSLR/DSLM-Kameras (**Nikon, Canon, Sony, Fuji**), Webcams und dem **Canon Selphy** Fotodrucker.

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

## Neu in Version 1.3.3: Selphy-Druck repariert 🖨️

- 🖨️ **Ganzes Foto auf der Karte**: Das Bild wird jetzt passend auf die 100×148 mm Selphy-Karte skaliert (vorher wurde nur ein Ausschnitt gedruckt).
- ✂️ **Randlos drucken** als Option im Admin-Menü → Drucker.
- 📶 **Drucken per WLAN**: Der Selphy wird im Netzwerk automatisch gefunden – damit druckt jetzt auch die Android-App.

## Neu in Version 1.3.2: Party-Look ✨

- 🎨 **Neue Testkamera** mit animierter Party-Szene (Lichterkette, Bokeh, Lumio-Logo) – sieht auch beim Testen ohne Kamera nach Fotobox aus.
- 🌙 **Ruhebildschirm im Akku-Sparmodus**: ein stimmungsvolles Party-Standbild statt schwarzem Hintergrund, auch rund ums Kamerabild im Hochformat.

## Neu in Version 1.3.1: Akku-Sparmodus & Hochformat 🔋

- 🔋 **Akku-Sparmodus**: Live-View nur noch ab dem Countdown – auf dem Startbildschirm ruht die Kamera. Gerade bei DSLRs hält der Akku so deutlich länger durch (Admin-Menü → Kamera).
- 📱 **Hochformat**: Lumio dreht sich mit dem Gerät und hat ein eigenes Hochkant-Layout. Das Live-Bild zeigt im Hochformat das komplette Kamerabild über die volle Breite.
- 📷 **Nikon D5600 am Handy** läuft jetzt rund: stabiler Live-View, Autofokus vor jedem Foto, automatisches Neuverbinden und volle Auflösung direkt aus der Kamera.

## Neu in Version 1.3.0: Lumio als Android-App 📱

Meine Fotobox soll auch ohne Laptop funktionieren – einfach ein altes Tablet oder Handy, Kamera per USB-C dran, fertig. Genau das geht jetzt:

- 📱 **Lumio läuft auf Android**: Gleiche Oberfläche und alle Modi wie auf Windows & Linux – Einzelfoto, 4er Collage, Doppelstreifen, GIF, Boomerang und Videobotschaft.
- 📷 **Nikon Z 50 II per USB-C (UVC/UAC)**: Einfach anstecken – Lumio bringt einen eigenen UVC-Treiber mit und braucht nicht, dass Android die Kamera selbst erkennt (beim Google Pixel macht es das z. B. nicht). Flüssiges Live-Bild mit 30 FPS, und für Videobotschaften wird gleich das Kamera-Mikro mitgenutzt.
- 📷 **Nikon D5600 per USB-OTG (PTP)**: Live-View und echte Auslösung direkt über USB.
- 🔌 **Anstecken und loslegen**: Eine angeschlossene Kamera hat automatisch Vorrang vor den eingebauten Handy-Kameras. Kamera ab- oder umgesteckt? Lumio verbindet sich von selbst neu.
- 🎬 **Videobotschaften mit Ton als MP4**: Läuft auf jedem Handy deiner Gäste.
- 🖥️ **Echter Kiosk-Vollbildmodus**: Keine Statusleiste, keine Navigationsleiste, Bildschirm bleibt an.

Was in 1.2.x dazugekommen ist (Touch-Admin-Menü mit Tabs, Bildschirmtastatur, USB-Stick-Erkennung, natives DSLR-Tethering), steht im **[CHANGELOG](CHANGELOG.md)**.

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
- **🔋 Akku-Sparmodus**: Auf Wunsch läuft das Live-Bild erst ab dem Countdown – die Kamera ruht, solange niemand fotografiert.
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
   - **Android (Tablets & Smartphones)**: Kiosk-App mit Vollbild-Modus, dauerhaft eingeschaltetem Bildschirm und Touch-Bedienung (siehe [Android-App](#android-app-))
2. **Kameras**:
   - **DSLR & Spiegellose Kameras**: Nikon, Canon, Sony, Fujifilm, Panasonic Lumix, Olympus
     - *Windows*: Über digiCamControl (kostenlos, MIT-Lizenz) oder direkte PTP-Erkennung
     - *Linux*: Nativ über gphoto2
     - *Android*:
       - **Nikon D5600 via USB-OTG (PTP)**: Eigener PTP-Treiber in C# – löst aus, streamt Live-View und holt das Foto direkt aufs Gerät. *(Tipp: Flugmodus an der Kamera einschalten, damit SnapBridge/Bluetooth den USB-Port nicht blockiert.)*
       - **Nikon Z 50 II via USB-C (UVC/UAC)**: Eigener UVC-Treiber – Live-Bild mit 30 FPS und Kamera-Mikro für Videobotschaften, auch auf Geräten ohne eigene USB-Kamera-Unterstützung (z. B. Google Pixel).
       - **Andere USB-Webcams & HDMI-Grabber**: Alles, was sich als UVC-Webcam mit MJPEG oder YUY2 meldet – auch ältere Kameras mit Clean-HDMI über einen 10–15 € USB-C Video-Grabber.
       - **Eingebaute Kameras**: Front- und Rückkamera des Tablets/Handys.
   - **Webcams**: USB-Webcams (Logitech, Anker etc.) sowie integrierte Surface-Front-/Rückkameras und Tablet-Kameras
   - **Simulierte Test-Kamera**: Integrierte virtuelle Testkamera mit Live-Zeitstempel zum Testen ohne Hardware
3. **Drucker**:
   - Canon Selphy CP1300 / CP1500 / CP1200 / CP1000 über den Windows-Druckertreiber (USB oder Netzwerk), CUPS auf Linux/Raspberry Pi oder direkt per WLAN (IPP) – auf Android automatisch per WLAN

---

## Schnellstart

### Fertige Version herunterladen (ohne Installation)
Einfach unter **[Releases](https://github.com/mazls/lumio/releases)** die passende ZIP-Datei laden:
- **`Lumio-win-x64.zip`** für normale Windows-Laptops und PCs
- **`Lumio-win-arm64.zip`** für Microsoft Surface mit ARM/Snapdragon-Prozessor
- **`Lumio-linux-x64.zip`** für **Linux PCs** (CachyOS, Arch Linux, Ubuntu, Debian, Fedora etc.)
- **`Lumio-linux-arm64.zip`** für **Raspberry Pi 4 / 5** (64-Bit OS)
- **`Lumio-android.apk`** für **Android-Tablets & -Handys** (Installation siehe [Android-App](#android-app-))

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

## Android-App 📱

Lumio gibt's auch als App für Android-Tablets und -Handys. **Ein Release im Google Play Store ist geplant** – bis dahin installierst du die App per Sideloading. Das klingt komplizierter als es ist:

### Variante 1: Direkt auf dem Gerät (am einfachsten)
1. Auf dem Tablet/Handy unter **[Releases](https://github.com/mazls/lumio/releases)** die Datei **`Lumio-android.apk`** herunterladen.
2. Die APK antippen. Android fragt beim ersten Mal, ob der Browser bzw. Dateimanager **„Unbekannte Apps installieren“** darf – erlauben.
3. Installieren, fertig.

### Variante 2: Über die Entwickleroptionen & Debugging (vom PC aus)
Praktisch, wenn du Lumio auf mehreren Geräten installierst oder selbst baust.

1. **Entwickleroptionen freischalten**: *Einstellungen → Über das Telefon* → **7× auf „Build-Nummer“ tippen** (danach ggf. PIN eingeben).
2. **Debugging einschalten**: *Einstellungen → System → Entwickleroptionen* →
   - **USB-Debugging** einschalten, wenn das Gerät per Kabel am PC hängt, oder
   - **Kabelloses Debugging** einschalten, wenn der USB-Port für die Kamera gebraucht wird (Gerät und PC im selben WLAN).
3. **ADB auf dem PC** installieren ([Android SDK Platform-Tools](https://developer.android.com/tools/releases/platform-tools)).
4. **Verbinden**:
   ```powershell
   # Per USB-Kabel: Gerät anstecken und die Debugging-Abfrage am Gerät bestätigen
   adb devices

   # Per WLAN: unter "Kabelloses Debugging" -> "Gerät über Kopplungscode koppeln"
   adb pair 192.168.x.x:KOPPEL-PORT KOPPLUNGSCODE
   adb connect 192.168.x.x:PORT      # Port steht oben unter "IP-Adresse & Port"
   ```
5. **Installieren**:
   ```powershell
   adb install -r Lumio-android.apk
   ```
   Wer aus dem Quellcode baut, erledigt Bauen, Installieren und Starten in einem Rutsch mit `install-android.bat` (bzw. `build-android.ps1 -Install`).

> **Update klappt nicht („App nicht installiert“)?** Dann wurde die alte Version mit einem anderen Schlüssel signiert. Alte Lumio-Version deinstallieren und neu installieren – **vorher die Fotos des letzten Events sichern**, die werden beim Deinstallieren mit gelöscht.

### Kamera anschließen
- **Nikon Z 50 II**: Im Kameramenü bei der USB-Verbindung **UVC/UAC** (USB-Webcam) wählen und per USB-C anstecken.
- **Nikon D5600**: Per USB-OTG-Adapter anstecken und den **Flugmodus an der Kamera** einschalten (sonst blockiert SnapBridge den USB-Port). Bildqualität auf **JPEG** oder **RAW+JPEG** stellen – reines RAW kann Lumio nicht verarbeiten.
- Beim ersten Anstecken fragt Android, ob Lumio geöffnet werden soll: **„Immer“ antippen** – dann kommt die USB-Abfrage nie wieder und die Kamera ist bei jedem Event sofort da.
- Lumio nimmt automatisch die angesteckte Kamera. Ohne externe Kamera wird die Front- bzw. Rückkamera des Geräts genutzt; im Admin-Menü unter **Kamera** kannst du jederzeit umschalten.
- Kamera- und Mikrofon-Zugriff beim ersten Start einfach erlauben.

### Tipps für den Event-Einsatz
- Ladegerät dranlassen – mit Kamera am USB-C-Port brauchst du dafür einen USB-C-Hub mit Power-Delivery.
- **Akku-Sparmodus** einschalten (Admin-Menü → Kamera) – dann hält auch der Kamera-Akku den ganzen Abend.
- Mit der Android-Funktion **„App-Fixierung“** (*Einstellungen → Sicherheit*) kommen die Gäste nicht aus Lumio heraus.

---

## Bedienung

1. **Start**: Software starten (`Lumio.exe` / `Lumio.bat` unter Windows, `./run-lumio.sh` unter Linux, App-Symbol auf Android).
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

# Android APK bauen (landet in publish-android\Lumio.apk):
powershell -ExecutionPolicy Bypass -File .\build-android.ps1
# oder einfach Doppelklick auf build-android.bat

# APK bauen, per ADB aufs verbundene Gerät installieren und starten:
.\install-android.bat
```

### Projektstruktur
- `src/Lumio.App`: WPF-Oberfläche für Windows (Touch-Kiosk, Themes, Dialoge)
- `src/Lumio.App.Shared`: Gemeinsame Cross-Platform Avalonia UI-Komponenten (Views, Modale, Styles, Assets)
- `src/Lumio.App.Avalonia`: Desktop-Starter für Windows, Linux & Raspberry Pi
- `src/Lumio.App.Android`: Android-App (.apk) für Tablets & Handys (PTP-Treiber für die D5600, eigener UVC/UAC-Treiber für die Z 50 II, Camera2, MP4-Videobotschaften, Kiosk-Modus)
- `src/Lumio.Core`: Session-Ablauf, State Machine, Einstellungen & Speicherlogik
- `src/Lumio.Camera`: Multi-Vendor DSLR-Treiber (Nikon, Canon, Sony via digiCamControl & gphoto2), Webcams & Testkamera
- `src/Lumio.Imaging`: SkiaSharp 300-DPI Collagen-Renderer & GIF/Boomerang/Video-Encoder
- `src/Lumio.Printing`: Canon Selphy Spooler & CUPS-Integration
- `tests/Lumio.Tests`: 31 automatisierte Unit-Tests für Core, Templates, Collagen, GIF & DSLR

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
