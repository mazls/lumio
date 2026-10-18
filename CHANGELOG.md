# Lumio Photobooth – Changelog 📜

Alle nennenswerten Änderungen und Versionen von Lumio werden in diesem Dokument festgehalten.

---

## [1.3.4] – 2026-10-18 – *Webcams auf Android, Update-Hinweis & Galerie mit Videos* 🎥

### 📷 Kameras
- **USB-Webcams auf Android**: Der eigene UVC-Treiber kann jetzt auch isochron übertragen – damit laufen die meisten normalen Webcams (getestet mit der Anker PowerConf C200) direkt am Handy oder Tablet. Gut für ein günstiges Tablet-Setup.
- **Robuster Videostream**: Klappt eine USB-Einstellung nicht oder kommt kein Bild, probiert Lumio selbst die nächste und schaltet notfalls auf eine kleinere Auflösung (z. B. bei wenig Bandbreite hinter einem USB-Hub). USB-3-Webcams werden mit der richtigen Paketgröße angesprochen.
- Fotos werden nur aus vollständig übertragenen Bildern gemacht; liefert eine Kamera dauerhaft kein Bild, versucht Lumio nicht mehr endlos neu zu verbinden.

### ✨ Oberfläche
- **Animierte Lichterkette**: Im Akku-Sparmodus zeigt der Startbildschirm den Party-Hintergrund mit sanft funkelnden Lichtern – vorher blieb dort manchmal das letzte Kamerabild stehen.
- **Randlos auf Android**: Der Hintergrund läuft bis an den Bildschirmrand (auch um die Frontkamera), die dunklen Balken oben und unten sind schmaler und weich.
- **Update-Hinweis**: Gibt es auf GitHub eine neue Version, erscheint auf dem Startbildschirm ein wegklickbarer Hinweis, im Admin-Menü → Allgemein steht die Version mit Download-Link. Nur mit Internet und nur bei echten Releases.
- Der Info-Dialog zeigt jetzt die richtige Versionsnummer.

### 🖼️ Galerie
- **Videobotschaften, GIFs und Boomerangs laufen in der Galerie** – auf Android und Windows mit Ton, auf Linux/Raspberry Pi über ffmpeg. Videobotschaften bekommen ein Vorschaubild für die Galerie-Kachel.

### 📂 Speichern
- **Ein Ordner pro Event**: Der Event-Ordner trägt das Datum des ersten Fotos und bleibt bestehen – auch über Mitternacht und mehrtägige Feiern hinweg. Neuer Ordner erst mit „Neues Event“ im Admin-Menü oder einem neuen Event-Namen.

---

## [1.3.3] – 2026-10-18 – *Selphy-Druck repariert* 🖨️

### 🖨️ Drucken
- **Ganzes Bild statt Ausschnitt**: Auf dem Selphy wurde nur ein Ausschnitt oben links gedruckt – das Foto wird jetzt passend auf die 100×148 mm Fotokarte skaliert und bei Bedarf gedreht.
- **Randlos drucken**: Neue Option im Admin-Menü → Drucker (standardmäßig an). Das Foto füllt die Karte, beschnitten wird nur minimal (~1 %). Ohne Haken wird das komplette Foto mit schmalem Rand gedruckt.
- **Papier wird an der Größe erkannt** (100×148 mm), egal wie der Treiber das Format nennt.
- Unter Windows wird über den modernen XPS-Druckweg mit Druck-Ticket gedruckt – damit kommt „randlos“ auch bei aktuellen Treibern wie dem Selphy-IPP-Treiber an.
- **Drucken auf Android**: Lumio findet den Selphy automatisch im WLAN und druckt direkt – ohne Android-Druckdialog.
- **WLAN-Drucker auch unter Windows & Linux**: Drucker im Netzwerk erscheinen zusätzlich als „(WLAN)“ in der Druckerauswahl und werden direkt per IPP angesteuert.

---

## [1.3.2] – 2026-10-18 – *Party-Look für Testkamera & Ruhebildschirm* ✨

### 🎨 Neue Hintergründe
- **Neue Testkamera**: Statt Smiley eine animierte Party-Szene in den Lumio-Farben – schwebende Bokeh-Lichter, funkelnde Lichterkette, Sucher-Ecken, drehendes Lumio-Blendenlogo, „TESTKAMERA“-Label und Live-Uhrzeit. Auch die Testfotos sehen jetzt so aus.
- **Ruhebildschirm im Akku-Sparmodus**: Statt eines schwarzen Hintergrunds liegt jetzt ein ruhiges Party-Standbild hinter dem Startbildschirm. Es wird nur einmal passend zur Bildschirmform gerendert und kostet keinen Akku.
- **Hochformat**: Über und unter dem Kamerabild füllt derselbe Hintergrund die Fläche statt schwarzer Balken – auf Android und Windows.
- Alles wird per Code gezeichnet: keine Bilddateien, gestochen scharf in jeder Auflösung.

### 🔐 Android
- Die APK im GitHub-Release ist ab sofort mit einem festen Release-Schlüssel signiert. Updates lassen sich direkt über die installierte Version installieren.

---

## [1.3.1] – 2026-10-18 – *Akku-Sparmodus, Hochformat & D5600-Feinschliff* 🔋

### 🔋 Akku-Sparmodus für den Live-View (alle Plattformen)
- **Neue Option im Admin-Menü → Kamera: „Akku-Sparmodus: Live-Bild erst ab dem Countdown“**:
  - Auf dem Startbildschirm ruht die Kamera – bei DSLRs bleibt der Spiegel unten, bei der Z 50 II stoppt der Video-Stream.
  - Mit „Foto starten“ wird der Live-View eingeschaltet („Kamera wird vorbereitet…“), der Countdown startet, sobald das erste Bild da ist.
  - Nach dem Ergebnis geht die Kamera automatisch wieder in den Ruhezustand.
  - Standardmäßig aus – ohne Haken bleibt alles wie gewohnt.

### 📱 Hochformat
- Lumio dreht sich mit dem Gerät (Rotationssperre wird respektiert).
- Eigenes Layout für Hochkant-Bildschirme: Modus-Buttons im 2×3-Raster, kompakter Start-Button, zweizeilige Ergebnis-Leiste.
- **Live-Bild passend zum Bildschirm**: Im Hochformat wird das komplette Kamerabild über die volle Breite gezeigt statt nur ein schmaler Streifen aus der Mitte – auch in der Windows-Version auf hochkant gedrehten Monitoren.
- Dialoge (Admin-Menü, PIN, Info) passen sich kleinen Bildschirmen an und scrollen bei Bedarf.
- Die Galerie-Kachel verdeckt im Ergebnis-Bildschirm nicht mehr den „Foto gespeichert“-Text.

### 📷 Nikon D5600 am Handy/Tablet
- Live-View läuft stabil (vorher wurde er nach jedem Bild sofort wieder beendet).
- Fotos werden zuverlässig in voller Auflösung direkt aus dem Kamera-Speicher geholt – keine doppelten Auslösungen mehr.
- Autofokus vor jedem Foto; kann die Kamera nicht scharfstellen, kommt ein klarer Hinweis statt eines Hängers.
- Bei RAW+JPEG wird automatisch das JPEG genommen; speichert die Kamera nur RAW, sagt Lumio Bescheid.
- Nach dem Aus- und Einschalten der Kamera verbindet sich Lumio von selbst wieder.
- Die gewählte Kamera wird dauerhaft gespeichert (vorher änderte sich ihre ID bei jedem Einstecken).

### 🛠️ Stabilität
- „Zurück in X“ zählt auf Android wieder herunter (Timer liefen auf manchen Geräten nicht).
- Ein doppeltes Antippen von „Foto starten“ startet keine zweite Aufnahme mehr.
- Unlesbare Kamerabilder führen zu einer verständlichen Meldung statt „Value cannot be null“.
- Die Android-APK aus dem GitHub-Release kann jetzt mit einem festen Release-Schlüssel signiert werden – künftige Updates lassen sich direkt drüber installieren.

---

## [1.3.0] – 2026-10-18 – *Die Android-App* 📱

### 📱 Lumio als Android-App (Tablets & Smartphones)
- **Neue Android-App (`Lumio-android.apk`)**:
  - Lumio läuft jetzt auch auf Android-Tablets und -Handys – gleiche Oberfläche, gleiche Modi wie auf Windows & Linux (Einzelfoto, 4er Collage, Doppelstreifen, GIF, Boomerang, Videobotschaft).
  - Gemeinsames UI-Projekt `Lumio.App.Shared` für Desktop (Windows/Linux/Raspberry Pi) und Android.
  - Installation per Sideloading (direkt oder über die Entwickleroptionen / USB- bzw. WLAN-Debugging). Ein Release im Google Play Store ist geplant.
- **Echter Kiosk-Vollbildmodus**:
  - Status- und Navigationsleiste werden komplett ausgeblendet (inkl. Kamera-Aussparung im Display) und nach Dialogen, App-Wechsel oder Drehen automatisch wieder versteckt.
  - Bildschirm bleibt während des Events dauerhaft an.
- **Kein Einfrieren mehr auf neuen Geräten**:
  - Auf manchen GPUs (z. B. Google Pixel 10 / PowerVR) blieb die Oberfläche nach dem Start hängen – Buttons reagierten nicht und der Countdown startete nie. Lumio rendert auf Android jetzt robust per Software-Rendering und hält die UI-Verarbeitung zuverlässig am Laufen.
  - Avalonia UI auf 11.3.22 aktualisiert.

### 📷 Kameras am Handy/Tablet
- **Nikon Z 50 II per USB-C (UVC/UAC)**:
  - Eigener UVC-Treiber direkt über USB-Host – funktioniert auch auf Geräten, die externe Kameras nicht selbst anbieten (z. B. Google Pixel).
  - Flüssiges MJPEG-Live-Bild mit 30 FPS, Fotos direkt aus dem Video-Stream.
  - Kamera-Mikrofon per USB-Audio (UAC) für Videobotschaften.
- **Nikon D5600 per USB-OTG (PTP)**:
  - USB-Berechtigungsabfrage für Android 14+ repariert (brach vorher mit einem Fehler ab).
  - Fotos werden auch ohne Speicherkarte direkt aus dem Kamera-Speicher geholt.
- **Automatische Kamera-Wahl & Hotplug**:
  - Eine angesteckte externe Kamera hat Vorrang vor den eingebauten Handy-Kameras.
  - Beim An- und Abstecken verbindet Lumio automatisch neu – ohne App-Neustart.
  - Beim Anstecken einer Kamera öffnet sich Lumio auf Wunsch automatisch („Immer mit Lumio öffnen“ = keine erneute USB-Abfrage).
- Kamera- und Mikrofon-Berechtigung werden direkt beim ersten Start abgefragt, damit später kein Dialog einen Gast unterbricht.

### 🎬 Videobotschaften auf Android
- Videobotschaften werden mit Ton aufgenommen (Kamera-Mikrofon per USB, sonst das Handy-Mikrofon).
- Speicherung als **MP4 (H.264 + AAC)** über die Hardware-Encoder des Geräts – läuft auf jedem Smartphone der Gäste.

### ⚙️ Build & Release
- Die GitHub-Release-Pipeline baut jetzt zusätzlich die Android-APK (`Lumio-android.apk`).

---

## [1.2.1] – 2026-10-17 – *Live-Hardware-Erkennung & Ultra-HD Grafik-Update* ✨

### 🔄 Kontinuierlicher Live-Scan im Admin-Menü (USB, Kamera & Drucker)
- **Automatischer 3-Sekunden-Hintergrundscan**:
  - USB-Sticks, Kameras (Webcams & DSLRs) und Drucker werden im geöffneten Admin-Menü alle 3 Sekunden im Hintergrund automatisch neu gesucht.
  - Das Schließen und erneute Öffnen des Admin-Menüs beim Einstecken eines USB-Sticks oder Anschließen einer Kamera/eines Druckers ist nicht mehr notwendig.
  - **Smarte Selektions-Erhaltung**: Bereits ausgewählte Geräte oder Pfade bleiben unverändert selektiert; neue USB-Sticks werden sofort erkannt und zur 1-Klick-Auswahl bereitgestellt.
- **Manueller 🔄 Schnell-Aktualisieren-Button**:
  - Jeweils neben der USB-Auswahl, Kamera-Auswahl und Drucker-Auswahl für sofortige Neu-Erkennung per Fingertipp.

### 🖼️ Ultra-HD 600 DPI & Entgrieselte Grafik
- **Anti-Banding Studio-Hintergrund in der Testkamera**:
  - 8-Bit-Farbbanding durch harmonischen Studio-Radialgradienten mit Dithering und Antialiasing vollständig eliminiert.
  - Testkamera-Vorschau von 1200×800 px auf native **1920×1280 px** (DSLR 3:2) hochgesetzt.
- **Ultra-HD Fotodruck & Collagen**:
  - Collagen und 10×15 cm Drucke werden jetzt in doppelter Pixeldichte mit **3600×2400 Pixeln** (600 DPI) und Mitchell-Netravali Bicubic-Sampling gerendert.
  - JPEG-Kompression auf nahezu verlustfreie **98 %** Qualitätsstufe angehoben (kein Rauschen/Mosquito Noise mehr in Farbverläufen).
- **Kristallklares UI-Rendering**:
  - WPF Hardware Layout-Rounding (`UseLayoutRounding="True"`) und Pixel-Snapping (`SnapsToDevicePixels="True"`) gegen unscharfe Kanten auf Windows-Skalierungen (125%/150%/200%).
  - Pixelscharfes Text-Rendering mit `TextFormattingMode="Display"` und `BitmapScalingMode="HighQuality"` für alle Bildansichten (LiveView, Review, Galerie).

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
