# Lumio Android APK Build Script
param([switch]$Install)

$dotnet = "$PSScriptRoot\.dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) {
    $dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
}
if (-not (Test-Path $dotnet)) {
    $cmd = Get-Command "dotnet" -ErrorAction SilentlyContinue
    if ($cmd) { $dotnet = $cmd.Source }
}

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "   Lumio Android APK Builder              " -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

$distDir = "$PSScriptRoot\publish-android"
if (-not (Test-Path $distDir)) {
    New-Item -ItemType Directory -Path $distDir -Force | Out-Null
}

Write-Host "Kompiliere Android Release APK..." -ForegroundColor Yellow
& $dotnet build "$PSScriptRoot\src\Lumio.App.Android\Lumio.App.Android.csproj" -c Release -p:AndroidPackageFormat=apk -nologo /nr:false

if ($LASTEXITCODE -ne 0) {
    Write-Host "Fehler beim Erstellen der Android APK!" -ForegroundColor Red
    exit 1
}

$apk = Get-ChildItem "$PSScriptRoot\src\Lumio.App.Android\bin\Release\net10.0-android" -Filter "*Signed.apk" -Recurse | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $apk) {
    $apk = Get-ChildItem "$PSScriptRoot\src\Lumio.App.Android\bin\Release\net10.0-android" -Filter "*.apk" -Recurse | Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

if ($apk) {
    $targetApk = "$distDir\Lumio.apk"
    Copy-Item $apk.FullName -Destination $targetApk -Force
    $sizeMb = [Math]::Round((Get-Item $targetApk).Length / 1MB, 1)

    Write-Host "`n✅ Android APK erfolgreich erstellt!" -ForegroundColor Green
    Write-Host "   Datei: $targetApk ($sizeMb MB)" -ForegroundColor White
    $adbExe = "adb"
    $foundAdb = Get-Command "adb" -ErrorAction SilentlyContinue
    if (-not $foundAdb) {
        $sdkAdb = "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe"
        if (Test-Path $sdkAdb) {
            $adbExe = $sdkAdb
            $foundAdb = $true
        }
    }

    if ($foundAdb) {
        Write-Host "  1. Option A (per USB / ADB): & `"$adbExe`" install -r `"$targetApk`"" -ForegroundColor Gray
    } else {
        Write-Host "  1. Option A (per USB / ADB): adb install -r `"$targetApk`"" -ForegroundColor Gray
    }
    Write-Host "  2. Option B (ohne PC): Datei 'Lumio.apk' aufs Tablet kopieren (z.B. per USB-Stick / Cloud / Mail) und antippen.`n" -ForegroundColor Gray

    if ($Install) {
        if ($foundAdb) {
            Write-Host "Installiere direkt auf dem Tablet via ADB..." -ForegroundColor Yellow
            & $adbExe install -r -d $targetApk
            Write-Host "Starte Lumio auf dem Tablet..." -ForegroundColor Yellow
            & $adbExe shell monkey -p io.github.mazls.lumio -c android.intent.category.LAUNCHER 1 | Out-Null
        } else {
            Write-Host "adb.exe nicht gefunden. Bitte SDK platform-tools zum PATH hinzufügen." -ForegroundColor Yellow
        }
    }
}
