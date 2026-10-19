# Lumio Google Play build: signed App Bundle (.aab) for the Play Console plus the matching signed APK,
# built exactly like the GitHub release (same release key = Play upload key).
#
#   .\build-play.ps1                         asks for the keystore passwords
#   .\build-play.ps1 -Keystore D:\keys\lumio-release.keystore -Alias lumio
#
# Passwords can also come from the environment (ANDROID_KEYSTORE_PASSWORD / ANDROID_KEY_PASSWORD),
# they are never written to disk or to the build log.
param(
    [string]$Keystore = "$PSScriptRoot\lumio-release.keystore",
    [string]$Alias = "lumio"
)

$ErrorActionPreference = "Stop"

$dotnet = "$PSScriptRoot\.dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) {
    $dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
}
if (-not (Test-Path $dotnet)) {
    $cmd = Get-Command "dotnet" -ErrorAction SilentlyContinue
    if ($cmd) { $dotnet = $cmd.Source }
}

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "   Lumio Google Play Build (AAB + APK)    " -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

if (-not (Test-Path $Keystore)) {
    Write-Host "Keystore nicht gefunden: $Keystore" -ForegroundColor Red
    Write-Host "Mit -Keystore <Pfad> angeben." -ForegroundColor Gray
    exit 1
}

function Read-Secret([string]$prompt) {
    $secure = Read-Host -Prompt $prompt -AsSecureString
    return [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
}

# Passed to MSBuild as "env:" references, so they never appear on the command line
if (-not $env:ANDROID_KEYSTORE_PASSWORD) {
    $env:ANDROID_KEYSTORE_PASSWORD = Read-Secret "Keystore-Passwort"
}
if (-not $env:ANDROID_KEY_PASSWORD) {
    $keyPassword = Read-Secret "Schlüssel-Passwort (leer = wie Keystore)"
    $env:ANDROID_KEY_PASSWORD = if ($keyPassword) { $keyPassword } else { $env:ANDROID_KEYSTORE_PASSWORD }
}

# Check keystore, password and alias before the long build: jarsigner only reports "exited with code 1"
$keytool = @(
    $(if ($env:JAVA_HOME) { "$env:JAVA_HOME\bin\keytool.exe" }),
    "C:\Program Files\Android\Android Studio\jbr\bin\keytool.exe",
    (Get-Command keytool -ErrorAction SilentlyContinue).Source
) + (Get-ChildItem "C:\Program Files\Microsoft\jdk-*\bin\keytool.exe" -ErrorAction SilentlyContinue).FullName |
    Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if ($keytool) {
    $check = & $keytool -list -keystore $Keystore -alias $Alias -storepass:env ANDROID_KEYSTORE_PASSWORD 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        Remove-Item Env:ANDROID_KEYSTORE_PASSWORD, Env:ANDROID_KEY_PASSWORD -ErrorAction SilentlyContinue
        if ($check -match "(?i)password|passwort|tampered|manipuliert") {
            Write-Host "Das Keystore-Passwort ist falsch." -ForegroundColor Red
        } elseif ($check -match "(?i)alias") {
            Write-Host "Der Alias '$Alias' existiert nicht im Keystore. Vorhandene Aliase: keytool -list -keystore `"$Keystore`"" -ForegroundColor Red
        } else {
            Write-Host "Keystore konnte nicht gelesen werden:`n$check" -ForegroundColor Red
        }
        exit 1
    }
    Write-Host "Keystore, Passwort und Alias '$Alias' passen." -ForegroundColor Green
}

$signing = @(
    "-p:AndroidKeyStore=true",
    "-p:AndroidSigningKeyStore=$Keystore",
    "-p:AndroidSigningStorePass=env:ANDROID_KEYSTORE_PASSWORD",
    "-p:AndroidSigningKeyAlias=$Alias",
    "-p:AndroidSigningKeyPass=env:ANDROID_KEY_PASSWORD"
)

$project = "$PSScriptRoot\src\Lumio.App.Android\Lumio.App.Android.csproj"
$distDir = "$PSScriptRoot\dist"
$work = "$PSScriptRoot\dist\play-build"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

# The Android build sometimes fails when an old obj folder is locked (e.g. by cloud sync): start clean
$obj = "$PSScriptRoot\src\Lumio.App.Android\obj\Release"
if (Test-Path $obj) { cmd /c rmdir /s /q "$obj" | Out-Null }

try {
    Write-Host "Baue App Bundle (.aab) für Google Play..." -ForegroundColor Yellow
    & $dotnet publish $project -c Release -f net10.0-android -p:AndroidPackageFormat=aab -o "$work\aab" -nologo @signing
    if ($LASTEXITCODE -ne 0) {
        throw "App Bundle konnte nicht gebaut werden. Meldet der Build 'jarsigner.exe wurde mit dem Code 1 beendet', ist das Schlüssel-Passwort falsch (Keystore-Passwort und Alias wurden bereits geprüft)."
    }

    Write-Host "Baue APK (zum direkten Installieren)..." -ForegroundColor Yellow
    & $dotnet publish $project -c Release -f net10.0-android -p:AndroidPackageFormat=apk -o "$work\apk" -nologo @signing
    if ($LASTEXITCODE -ne 0) { throw "APK konnte nicht gebaut werden." }
}
finally {
    Remove-Item Env:ANDROID_KEYSTORE_PASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:ANDROID_KEY_PASSWORD -ErrorAction SilentlyContinue
}

$aab = Get-ChildItem "$work\aab" -Filter "*-Signed.aab" | Select-Object -First 1
$apk = Get-ChildItem "$work\apk" -Filter "*-Signed.apk" | Select-Object -First 1
if (-not $aab -or -not $apk) {
    Write-Host "Signierte Dateien wurden nicht gefunden." -ForegroundColor Red
    exit 1
}

Copy-Item $aab.FullName "$distDir\Lumio-android.aab" -Force
Copy-Item $apk.FullName "$distDir\Lumio-android.apk" -Force
Remove-Item $work -Recurse -Force

$version = ([xml](Get-Content "$PSScriptRoot\src\Lumio.App.Android\AndroidManifest.xml")).manifest
Write-Host "`n✅ Fertig – Version $($version.versionName) (Versionscode $($version.versionCode))" -ForegroundColor Green
Write-Host "   Google Play:  $distDir\Lumio-android.aab" -ForegroundColor White
Write-Host "   Sideloading:  $distDir\Lumio-android.apk" -ForegroundColor White
Write-Host "`nIn der Play Console unter 'Testen und veröffentlichen' ein Release anlegen und die .aab hochladen." -ForegroundColor Gray
Write-Host "Jeder Upload braucht einen höheren Versionscode als der vorherige." -ForegroundColor Gray
