@echo off
set "EXE=%~dp0publish\Lumio.exe"
if not exist "%EXE%" (
    powershell -ExecutionPolicy Bypass -File "%~dp0start-lumio.ps1"
    exit /b
)
start "" /D "%~dp0publish" "%EXE%"
