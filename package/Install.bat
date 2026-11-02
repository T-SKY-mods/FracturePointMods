@echo off
rem Installs all Fracture Point MODs: finds the game, backs up your save, installs MelonLoader (if needed) and the mods.
rem Turn off the mods you do not want in the game: Settings > MOD.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Installer\install.ps1" %*
echo.
pause
