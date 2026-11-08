# Removes all Fracture Point MODs without questions (called by "Uninstall.bat" / "Uninstall (also MelonLoader).bat")
#  - stops if a survival run still keeps your items (they are only returned by the mod, in your room)
#  - removes Mods\<mod>.dll and the models (UserData\<mod>\models)
#  - keeps the mod data (weapon rolls, mastery, attachments, survival record): installing again brings them back
#  - resets the switches in Settings > MOD (UserData\FPModOption\settings.json): installing again starts with all mods ON
#  - -RemoveMelon: also removes MelonLoader (version.dll, MelonLoader\, UserLibs\, empty Mods\ and Plugins\) = fully vanilla game.
#    Skipped when other mods or plugins are still installed, or when MelonLoader was there before these mods
#    (it was not installed by Install.bat). MelonLoader lives in this game's folder only: other games are never affected.
#  The game's own save is never touched.
param([string]$GameDir = '', [switch]$RemoveMelon)
$ErrorActionPreference = 'Stop'
$Mods   = 'FPModOption', 'FPRarity', 'FPSurvival', 'FPCustom', 'FPOptimize', 'FPHitMarker'
$Models = 'FPRarity', 'FPCustom'

. (Join-Path $PSScriptRoot 'common.ps1')

Write-Host 'Fracture Point MODs uninstaller' -ForegroundColor White
$GameDir = Resolve-GameDir $GameDir
if (Get-Process -Name 'Fracture Point Release' -ErrorAction SilentlyContinue) { throw 'The game is running. Please quit it from the menu and run the uninstaller again.' }

if (Test-Path (Join-Path $GameDir 'UserData\FPSurvival\pending.json')) {
    Write-Host ''
    Write-Host '   Not removed: a survival run still keeps your items (money, ammo, loot...).' -ForegroundColor Red
    Write-Host '   Start the game with the mods installed (survival mode ON in Settings > MOD) and enter your room first' -ForegroundColor Red
    Write-Host '   (the items are returned there), quit from the menu, then run this again.' -ForegroundColor Red
    exit 1
}

Step 'Removing the mods'
foreach ($m in $Mods) {
    $f = Join-Path $GameDir "Mods\$m.dll"
    if (Test-Path $f) { Remove-Item -Force $f; Ok "Removed Mods\$m.dll" } else { Ok "Mods\$m.dll was not installed." }
}
foreach ($m in $Models) {
    $d = Join-Path $GameDir "UserData\$m\models"
    if (Test-Path $d) { Remove-Item -Recurse -Force $d; Ok "Removed UserData\$m\models" }
}
# MOD のオン/オフは消す（入れ直したら、初めて入れたときと同じくすべてオンから始める）
$settings = Join-Path $GameDir 'UserData\FPModOption\settings.json'
if (Test-Path $settings) { Remove-Item -Force $settings; Ok 'Reset the MOD switches (all mods are ON when installed again).' }
Ok 'Kept the mod data (UserData\FPRarity, FPSurvival, FPCustom, FPOptimize). Installing again brings it back.'

if ($RemoveMelon) {
    Step 'MelonLoader'
    $others = @(Get-ChildItem (Join-Path $GameDir 'Mods') -Filter *.dll -ErrorAction SilentlyContinue)
    $plugins = @(Get-ChildItem (Join-Path $GameDir 'Plugins') -File -Recurse -ErrorAction SilentlyContinue)
    $marker = Join-Path $GameDir 'UserData\FPModOption\melonloader_installed_by_fpmods.txt'
    if ($others.Count -gt 0) {
        Warn ('Other mods are still installed (' + (($others | ForEach-Object Name) -join ', ') + '), so MelonLoader was kept.')
    }
    elseif ($plugins.Count -gt 0) {
        Warn ('MelonLoader plugins are still installed (' + (($plugins | ForEach-Object Name) -join ', ') + '), so MelonLoader was kept.')
    }
    elseif (-not (Test-Path $marker)) {
        Warn 'MelonLoader was installed before these mods (not by Install.bat), so it was kept.'
        Warn 'To remove it anyway, delete version.dll, MelonLoader, UserLibs, Mods and Plugins in the game folder.'
    }
    else {
        foreach ($p in 'version.dll', 'MelonLoader', 'UserLibs') {
            $t = Join-Path $GameDir $p
            if (Test-Path $t) { Remove-Item -Recurse -Force $t; Ok "Removed $p" }
        }
        foreach ($p in 'Mods', 'Plugins') {
            $t = Join-Path $GameDir $p
            if ((Test-Path $t) -and @(Get-ChildItem $t -Recurse -Force -ErrorAction SilentlyContinue).Count -eq 0) { Remove-Item -Force $t; Ok "Removed the empty $p folder" }
        }
        Remove-Item -Force $marker
    }
}

Write-Host ''
Write-Host 'Done. The game now runs without these mods (your game save was not changed).' -ForegroundColor Green
