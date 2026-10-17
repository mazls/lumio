# Lumio Starter Script
param([switch]$Rebuild)

$exe = "$PSScriptRoot\publish\Lumio.exe"

# Detect system architecture (Surface ARM64 vs Intel/AMD x64)
$arch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) { "win-arm64" } else { "win-x64" }

# Detect whether sources are newer than published executable or architecture mismatch
$needsPublish = $Rebuild -or (-not (Test-Path $exe))
if (-not $needsPublish -and (Test-Path $exe)) {
    # Check if executable architecture matches current machine
    try {
        $bytes = [System.IO.File]::ReadAllBytes($exe)
        $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
        $machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
        $expectedMachine = if ($arch -eq "win-arm64") { 0xAA64 } else { 0x8664 }
        if ($machine -ne $expectedMachine) {
            Write-Host "Architektur-Wechsel erkannt ($arch). Neu-Kompilierung erforderlich..." -ForegroundColor Yellow
            $needsPublish = $true
        }
    } catch {
        $needsPublish = $true
    }

    if (-not $needsPublish) {
        $exeTime = (Get-Item $exe).LastWriteTime
        $sources = Get-ChildItem -Path "$PSScriptRoot\src" -Recurse -Include *.cs,*.xaml,*.axaml,*.csproj
        $latestSource = ($sources | Measure-Object -Property LastWriteTime -Maximum).Maximum
        if ($latestSource -gt $exeTime) {
            $needsPublish = $true
        }
    }
}

if ($needsPublish) {
    # 1. Locate dotnet (check local portable folder, user appdata, then system PATH)
    $dotnet = "$PSScriptRoot\.dotnet\dotnet.exe"
    if (-not (Test-Path $dotnet)) {
        $dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
    }
    if (-not (Test-Path $dotnet)) {
        $cmd = Get-Command "dotnet" -ErrorAction SilentlyContinue
        if ($cmd) { $dotnet = $cmd.Source }
    }

    # 2. If dotnet is still not found, automatically download portable .NET 10 (no manual install needed!)
    $isDotnetWorking = $false
    if (Test-Path $dotnet) {
        try {
            $ver = & $dotnet --version 2>$null
            if ($ver -and $ver.StartsWith("10.")) { $isDotnetWorking = $true }
        } catch { }
    }

    if (-not $isDotnetWorking) {
        Write-Host "Kein .NET 10 SDK auf dem System gefunden." -ForegroundColor Yellow
        Write-Host "Lade portable .NET Runtime/SDK automatisch von Microsoft herunter..." -ForegroundColor Cyan
        $installScript = "$env:TEMP\dotnet-install.ps1"
        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13
            Invoke-WebRequest -Uri "https://dot.net/v1/dotnet-install.ps1" -OutFile $installScript -UseBasicParsing
            $localInstallDir = "$PSScriptRoot\.dotnet"
            & $installScript -Channel 10.0 -InstallDir $localInstallDir -NoPath
            $dotnet = "$localInstallDir\dotnet.exe"
            Write-Host "Portable .NET Runtime erfolgreich eingerichtet!" -ForegroundColor Green
        } catch {
            Write-Warning "Automatischer Download fehlgeschlagen: $_"
        }
    }

    Write-Host "Lumio wird gebaut und als vollständiges Standalone-Paket gebündelt ($arch)..." -ForegroundColor Cyan

    # Sauberen Publish-Ordner gewährleisten, um gemischte Architekturen (x64 / ARM64) zu verhindern
    if (Test-Path "$PSScriptRoot\publish") {
        Get-ChildItem -Path "$PSScriptRoot\publish" -Recurse | Remove-Item -Force -Recurse -ErrorAction SilentlyContinue
    } else {
        New-Item -ItemType Directory -Path "$PSScriptRoot\publish" -Force | Out-Null
    }

    & $dotnet publish "$PSScriptRoot\src\Lumio.App\Lumio.App.csproj" -c Release -r $arch --self-contained true -o "$PSScriptRoot\publish" -nologo
    $exe = "$PSScriptRoot\publish\Lumio.exe"
}

Write-Host "Lumio wird gestartet..." -ForegroundColor Green
Start-Process $exe -WorkingDirectory "$PSScriptRoot\publish"
