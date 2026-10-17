@echo off
powershell -ExecutionPolicy Bypass -File "%~dp0build-android.ps1" -Install
pause
