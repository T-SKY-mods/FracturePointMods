# Fracture Point MODs installer (called by "Install.bat")
#  1. finds the Fracture Point folder (Steam library), or asks for it
#  2. backs up the game save (LocalLow + registry) and the mod data to Documents\FPMods_backup\<date>
#  3. installs MelonLoader 0.7.3 if it is not installed yet (official GitHub release, SHA256 checked)
#  4. copies ALL mods (Mods\*.dll) and their models (UserData\<mod>\models).
#     Turn off the mods you don't want in the game: Settings > MOD (applied after a restart).
#  Installing over an older version keeps the mod data (weapon rolls, mastery, attachments, survival record) and the MOD switches.
#  A first install (or one after Uninstall.bat) starts with all mods ON.
param([string]$GameDir = '', [switch]$DetectOnly)
$ErrorActionPreference = 'Stop'

$Pkg       = Split-Path -Parent $PSScriptRoot           # package root (folder with Mods\, UserData\)
$MlUrl     = 'https://github.com/LavaGang/MelonLoader/releases/download/v0.7.3/MelonLoader.x64.zip'
$MlSha256  = '5B2B2F3D1CD42B59EC886C5BDC2663EDAE87A0097A4F4A8F58C0965A99DDA416'
$SaveDir   = Join-Path $env:USERPROFILE 'AppData\LocalLow\Kyrylo Burlaka\Fracture Point Release'
$RegKey    = 'HKCU\Software\Kyrylo Burlaka\Fracture Point Release'
$Mods      = 'FPModOption', 'FPRarity', 'FPSurvival', 'FPCustom', 'FPOptimize'
$Models    = 'FPRarity', 'FPCustom'

. (Join-Path $PSScriptRoot 'common.ps1')

Write-Host 'Fracture Point MODs 1.1.0 installer' -ForegroundColor White

# --- 1. game folder ---
$GameDir = Resolve-GameDir $GameDir
if ($DetectOnly) { return }

if (Get-Process -Name 'Fracture Point Release' -ErrorAction SilentlyContinue) { throw 'The game is running. Please quit it from the menu and run the installer again.' }
foreach ($m in $Mods) { if (-not (Test-Path (Join-Path $Pkg "Mods\$m.dll"))) { throw "Missing package file: Mods\$m.dll (extract the whole zip first)" } }
foreach ($m in $Models) { if (-not (Test-Path (Join-Path $Pkg "UserData\$m\models"))) { throw "Missing package file: UserData\$m\models (extract the whole zip first)" } }

# --- 2. backup ---
Step 'Backing up your game save'
$bk = Join-Path ([Environment]::GetFolderPath('MyDocuments')) ("FPMods_backup\" + (Get-Date -Format 'yyyyMMdd_HHmmss'))
New-Item -ItemType Directory -Force $bk | Out-Null
if (Test-Path $SaveDir) { Copy-Item -Recurse $SaveDir (Join-Path $bk 'LocalLow') }
cmd /c "reg export `"$RegKey`" `"$bk\PlayerPrefs.reg`" /y >nul 2>&1"
# mod data (everything except models and logs)
foreach ($m in $Mods) {
    $d = Join-Path $GameDir "UserData\$m"
    if (-not (Test-Path $d)) { continue }
    $files = @(Get-ChildItem $d -File -Recurse | Where-Object { $_.FullName -notmatch '\\models\\' -and $_.Extension -ne '.log' })
    foreach ($f in $files) {
        $rel = $f.FullName.Substring($d.Length + 1)
        $out = Join-Path $bk "UserData\$m\$rel"
        New-Item -ItemType Directory -Force (Split-Path -Parent $out) | Out-Null
        Copy-Item $f.FullName $out
    }
}
Ok "Backup: $bk"

# --- 3. MelonLoader ---
Step 'MelonLoader'
$mlInstalled = (Test-Path (Join-Path $GameDir 'version.dll')) -and (Test-Path (Join-Path $GameDir 'MelonLoader\net6\MelonLoader.dll'))
$marker = Join-Path $GameDir 'UserData\FPModOption\melonloader_installed_by_fpmods.txt'
if ($mlInstalled) {
    $mlVer = (Get-Item (Join-Path $GameDir 'MelonLoader\net6\MelonLoader.dll')).VersionInfo.FileVersion
    Ok "Already installed (kept as is). Version: $mlVer"
    if ($mlVer -and ([version]($mlVer -replace '[^0-9.].*$', '')) -lt [version]'0.7.3') {
        Warn 'These mods are made for MelonLoader 0.7.3. If the game does not start, update MelonLoader to 0.7.3.'
    }
}
else {
    $zip = Join-Path $env:TEMP 'MelonLoader.x64.v0.7.3.zip'
    Write-Host '   Downloading MelonLoader 0.7.3 from GitHub (about 20 MB)...'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $MlUrl -OutFile $zip -UseBasicParsing
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
    if ($hash -ne $MlSha256) { Remove-Item -Force $zip; throw "MelonLoader download check failed (SHA256 $hash). Please install it manually (see README)." }
    Expand-Archive -Path $zip -DestinationPath $GameDir -Force
    Remove-Item -Force $zip
    # この installer が入れた印（Uninstall (also MelonLoader).bat は、印があるときだけ MelonLoader を消す）
    New-Item -ItemType Directory -Force (Split-Path -Parent $marker) | Out-Null
    Set-Content -Path $marker -Value 'MelonLoader 0.7.3 was installed by the Fracture Point MODs installer.' -Encoding ascii
    Ok 'Installed MelonLoader 0.7.3.'
}

# --- 4. mod files ---
Step 'Installing the mods'
New-Item -ItemType Directory -Force (Join-Path $GameDir 'Mods') | Out-Null
foreach ($m in $Mods) {
    Copy-Item -Force (Join-Path $Pkg "Mods\$m.dll") (Join-Path $GameDir "Mods\$m.dll")
    Ok "Mods\$m.dll"
}
foreach ($m in $Models) {
    $dst = Join-Path $GameDir "UserData\$m\models"
    New-Item -ItemType Directory -Force (Split-Path -Parent $dst) | Out-Null
    if (Test-Path $dst) { Remove-Item -Recurse -Force $dst }
    Copy-Item -Recurse (Join-Path $Pkg "UserData\$m\models") $dst
    Ok "UserData\$m\models"
}
# zip から出したファイルの「インターネットから来た」印を外す（Windows の警告で読み込みが止まらないように）
foreach ($m in $Mods) { Unblock-File -Path (Join-Path $GameDir "Mods\$m.dll") -ErrorAction SilentlyContinue }
foreach ($m in $Models) { Get-ChildItem (Join-Path $GameDir "UserData\$m\models") -File -Recurse | Unblock-File -ErrorAction SilentlyContinue }
$keptSwitches = Test-Path (Join-Path $GameDir 'UserData\FPModOption\settings.json')
Ok 'Your mod data was kept.'

Write-Host ''
Write-Host 'Done! Start the game from Steam.' -ForegroundColor Green
if ($keptSwitches) { Write-Host 'Your MOD switches were kept. Change them in Settings > MOD (applied after a restart).' -ForegroundColor Green }
else { Write-Host 'All mods are ON at first. Turn off the ones you do not want in Settings > MOD (applied after a restart).' -ForegroundColor Green }
if (-not $mlInstalled) { Write-Host 'The first start takes a few minutes (MelonLoader prepares the game). That is normal.' -ForegroundColor Yellow }
