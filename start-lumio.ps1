# Lumio Starter Script
$exe = "$PSScriptRoot\publish\Lumio.exe"

if (-not (Test-Path $exe)) {
    Write-Host "Lumio wird als Standalone vorbereitet..." -ForegroundColor Cyan
    $dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
    if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }
    & $dotnet publish "$PSScriptRoot\src\Lumio.App\Lumio.App.csproj" -c Release -r win-arm64 --self-contained true -o "$PSScriptRoot\publish" -nologo
    $exe = "$PSScriptRoot\publish\Lumio.exe"
}

Write-Host "Lumio wird gestartet..." -ForegroundColor Green
Start-Process $exe -WorkingDirectory "$PSScriptRoot\publish"
