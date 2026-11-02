@echo off
rem Removes all Fracture Point MODs and MelonLoader (fully vanilla game). Mod data is kept.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Installer\uninstall.ps1" -RemoveMelon %*
echo.
pause
