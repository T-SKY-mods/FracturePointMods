# Shared helpers for install.ps1 / uninstall.ps1
$ExeName = 'Fracture Point Release.exe'

function Step($t) { Write-Host ''; Write-Host "== $t" -ForegroundColor Cyan }
function Ok($t)   { Write-Host "   $t" -ForegroundColor Green }
function Warn($t) { Write-Host "   $t" -ForegroundColor Yellow }

# Looks for "<Steam library>\steamapps\common\Fracture Point" in every Steam library folder
function Find-GameDir {
    $roots = New-Object System.Collections.Generic.List[string]
    foreach ($k in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam') {
        try {
            $p = Get-ItemProperty $k -ErrorAction Stop
            foreach ($v in @($p.SteamPath, $p.InstallPath)) { if ($v) { $roots.Add(($v -replace '/', '\')) } }
        } catch { }
    }
    $roots.Add('C:\Program Files (x86)\Steam')
    $libs = New-Object System.Collections.Generic.List[string]
    foreach ($r in ($roots | Select-Object -Unique)) {
        $libs.Add($r)
        $vdf = Join-Path $r 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) { $libs.Add(($m.Groups[1].Value -replace '\\\\', '\')) }
        }
    }
    foreach ($l in ($libs | Select-Object -Unique)) {
        $d = Join-Path $l 'steamapps\common\Fracture Point'
        if (Test-Path (Join-Path $d $ExeName)) { return $d }
    }
    return $null
}

# Uses the given folder, else the Steam library, else asks the user
function Resolve-GameDir([string]$dir) {
    Step 'Looking for the game folder'
    if (-not $dir) { $dir = Find-GameDir }
    while (-not $dir -or -not (Test-Path (Join-Path $dir $ExeName))) {
        if ($dir) { Warn "Not found: $dir\$ExeName" }
        $dir = (Read-Host '   Enter the Fracture Point folder (the one with "Fracture Point Release.exe")').Trim('"', ' ')
        if (-not $dir) { throw 'No game folder given.' }
    }
    Ok "Game folder: $dir"
    return $dir
}
