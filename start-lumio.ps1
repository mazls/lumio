# Lumio Starter Script
param([switch]$Rebuild)

$exe = "$PSScriptRoot\publish\Lumio.exe"

# Detect whether sources are newer than published executable
$needsPublish = $Rebuild -or (-not (Test-Path $exe))
if (-not $needsPublish -and (Test-Path $exe)) {
    $exeTime = (Get-Item $exe).LastWriteTime
    $sources = Get-ChildItem -Path "$PSScriptRoot\src" -Recurse -Include *.cs,*.xaml,*.axaml,*.csproj
    $latestSource = ($sources | Measure-Object -Property LastWriteTime -Maximum).Maximum
    if ($latestSource -gt $exeTime) {
        $needsPublish = $true
    }
}

if ($needsPublish) {
    # Dynamically detect system architecture (Surface ARM64 vs normal Intel/AMD x64)
    $arch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) { "win-arm64" } else { "win-x64" }
    Write-Host "Lumio wird gebaut und aktualisiert (Release $arch)..." -ForegroundColor Cyan
    $dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
    if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }
    & $dotnet publish "$PSScriptRoot\src\Lumio.App\Lumio.App.csproj" -c Release -r $arch --self-contained true -o "$PSScriptRoot\publish" -nologo
    $exe = "$PSScriptRoot\publish\Lumio.exe"
}

Write-Host "Lumio wird gestartet..." -ForegroundColor Green
Start-Process $exe -WorkingDirectory "$PSScriptRoot\publish"
