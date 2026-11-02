@echo off
rem Removes all Fracture Point MODs right away (no questions). Mod data and MelonLoader are kept.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Installer\uninstall.ps1" %*
echo.
pause
