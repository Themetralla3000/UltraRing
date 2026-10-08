<#
.SYNOPSIS
  Installs the UltraRing bridge into the Elden Ring and ULTRAKILL game folders (run tools\Build.ps1 first).
.DESCRIPTION
  Elden Ring (host), like Minecraft Ring's Install.ps1:
    - refuses to run while eldenring.exe is running
    - backs up %APPDATA%\EldenRing (saves) and moves conflicting mod files out of the game folder into backups\<timestamp>
    - copies dinput8.dll and erbridge\erbridge_core.dll, writes steam_appid.txt (1245620)
  ULTRAKILL (guest) is delegated to the ULTRAKILL Crossover Bridge kit (bridge\scripts\Install-Guest.ps1): it copies
    BepInEx's winhttp.dll (Doorstop proxy) and a doorstop_config.ini with enabled=false next to ULTRAKILL.exe, so a
    normal Steam launch stays vanilla; Launch.ps1 enables BepInEx for its own launch only. Backups of existing files
    go to the kit's runtime\backups.
  Everything is recorded in installation.json (used by Launch.ps1 and Restore.ps1; the kit keeps its own
  bridge\runtime\guest-install.json). Running Install.ps1 again for
  the same folders only refreshes the files; the original backups are kept.
.PARAMETER EldenRingDir
  The folder containing eldenring.exe (default: the Steam path).
.PARAMETER UltrakillDir
  The folder containing ULTRAKILL.exe (default: the Steam path).
.PARAMETER UltrakillSteamAppId
  Also write steam_appid.txt (1229490) next to ULTRAKILL.exe. Off by default: ULTRAKILL uses Facepunch.Steamworks and
  never calls RestartAppIfNecessary, so starting ULTRAKILL.exe directly (Steam running) works without it. Use this
  only if the launched ULTRAKILL exits at once or "restarts through Steam" (which would lose the BepInEx arguments).
.PARAMETER SkipEldenRing
  Install only the ULTRAKILL side.
.PARAMETER SkipUltrakill
  Install only the Elden Ring side.
#>
param(
    [string]$EldenRingDir = 'C:\Program Files (x86)\Steam\steamapps\common\ELDEN RING\Game',
    [string]$UltrakillDir = 'C:\Program Files (x86)\Steam\steamapps\common\ULTRAKILL',
    [switch]$UltrakillSteamAppId,
    [switch]$SkipEldenRing,
    [switch]$SkipUltrakill
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = $PSScriptRoot
. (Join-Path $PSScriptRoot 'tools\BridgeCommon.ps1')
$RecordPath = Join-Path $ProjectRoot 'installation.json'

function Resolve-Dir([string]$Path) { [IO.Path]::GetFullPath($Path).TrimEnd('\') }
function Assert-Inside([string]$Dir, [string]$Candidate) {
    if (-not $Candidate.StartsWith($Dir + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Invalid path outside the game folder: $Candidate" }
}

# ---------------------------------------------------------------- checks
if ($SkipEldenRing -and $SkipUltrakill) { throw 'Nothing to install.' }
$NativeDist = Join-Path $ProjectRoot 'external\minecraft-ring\dist'
$Kit = Join-Path $ProjectRoot 'bridge'
$KitInstall = Join-Path $Kit 'scripts\Install-Guest.ps1'
$UkStage = Join-Path $Kit 'dist\guest'
$KitRecord = Join-Path $Kit 'runtime\guest-install.json' 
if (-not $SkipEldenRing) {
    $EldenRingDir = Resolve-Dir $EldenRingDir
    if (Get-Process eldenring -ErrorAction SilentlyContinue) { throw 'Close Elden Ring before installation.' }
    if (-not (Test-Path -LiteralPath "$EldenRingDir\eldenring.exe")) { throw "eldenring.exe was not found in '$EldenRingDir'. Pass -EldenRingDir." }
    foreach ($Name in 'dinput8.dll', 'erbridge_core.dll') {
        if (-not (Test-Path -LiteralPath "$NativeDist\$Name")) { throw "Missing $NativeDist\$Name. Run tools\Build.ps1 first." }
    }
}
if (-not $SkipUltrakill) {
    $UltrakillDir = Resolve-Dir $UltrakillDir
    if (Get-Process ULTRAKILL -ErrorAction SilentlyContinue) { throw 'Close ULTRAKILL before installation.' }
    if (-not (Test-Path -LiteralPath "$UltrakillDir\ULTRAKILL.exe")) { throw "ULTRAKILL.exe was not found in '$UltrakillDir'. Pass -UltrakillDir." }
    if (-not (Test-Path -LiteralPath $KitInstall)) { throw 'The ULTRAKILL Crossover Bridge submodule (bridge\) is not initialised. Run: git submodule update --init' }
    if (-not (Test-Path -LiteralPath "$UkStage\winhttp.dll") -or -not (Test-Path -LiteralPath "$UkStage\BepInEx\core\BepInEx.Preloader.dll")) {
        throw "The guest is not staged at $UkStage. Run tools\Build.ps1 first."
    }
}

# Previous installation of the same folders: refresh the files, keep the original backups.
$Prev = $null
if (Test-Path -LiteralPath $RecordPath) {
    try { $Prev = Get-Content -LiteralPath $RecordPath -Raw | ConvertFrom-Json } catch { $Prev = $null }
}
$PrevEr = $null
$PrevUk = $null
if ($Prev) {
    if ($Prev.eldenring -and -not $SkipEldenRing -and ([string]$Prev.eldenring.game_dir) -ieq $EldenRingDir) { $PrevEr = $Prev.eldenring }
    if ($Prev.ultrakill -and -not $SkipUltrakill -and ([string]$Prev.ultrakill.game_dir) -ieq $UltrakillDir) { $PrevUk = $Prev.ultrakill }
}

$BackupRoot = Join-Path $ProjectRoot ('backups\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $BackupRoot -Force | Out-Null
$Record = [ordered]@{ installed_at = (Get-Date).ToString('o'); eldenring = $null; ultrakill = $null }
if ($Prev) {
    # keep the half of the record we are not touching
    if ($SkipEldenRing -and $Prev.eldenring) { $Record.eldenring = $Prev.eldenring }
    if ($SkipUltrakill -and $Prev.ultrakill) { $Record.ultrakill = $Prev.ultrakill }
}

# ---------------------------------------------------------------- Elden Ring (host)
if (-not $SkipEldenRing) {
    if ($PrevEr) {
        $ErBackup = [string]$PrevEr.backup
        $Moved = @($PrevEr.moved_mods)
        $HadAppId = [bool]$PrevEr.had_steam_appid
        $OrigScreenMode = [string]$PrevEr.original_screen_mode
        Write-Output 'Elden Ring: already installed here, refreshing the bridge files.'
    } else {
        $ErBackup = Join-Path $BackupRoot 'eldenring'
        New-Item -ItemType Directory -Path $ErBackup -Force | Out-Null
        $SaveRoot = Join-Path $env:APPDATA 'EldenRing'
        # Saves + GraphicsConfig.xml are copied FIRST, before any file is moved and before Launch.ps1 ever edits ScreenMode.
        $OrigScreenMode = $null
        if (Test-Path -LiteralPath $SaveRoot) {
            Copy-Item -LiteralPath $SaveRoot -Destination "$ErBackup\saves" -Recurse
            if (-not (Test-Path -LiteralPath "$ErBackup\saves")) { throw "The save backup failed ($ErBackup\saves); nothing was changed." }
            try { $OrigScreenMode = Get-ScreenMode "$ErBackup\saves\GraphicsConfig.xml" } catch { $OrigScreenMode = $null }
            Write-Output "Elden Ring: saves backed up to $ErBackup\saves (original ScreenMode: $(if ($OrigScreenMode) { $OrigScreenMode } else { 'unknown' }))."
        }
        $ModNames = @('dinput8.dll', 'dxgi.dll', 'd3d11.dll', 'winmm.dll', 'version.dll', 'modengine2.dll', 'modengine2', 'mods', 'mod', 'SeamlessCoop', 'ersc_launcher.exe', 'ersc.dll', 'ersc_settings.ini', 'mod_loader_config.ini', 'ReShade.ini', 'ReShadePreset.ini', 'reshade-shaders', 'erbridge')
        $Moved = @()
        foreach ($Name in $ModNames) {
            $Candidate = [IO.Path]::GetFullPath((Join-Path $EldenRingDir $Name))
            Assert-Inside $EldenRingDir $Candidate
            if (Test-Path -LiteralPath $Candidate) {
                Move-Item -LiteralPath $Candidate -Destination (Join-Path $ErBackup $Name)
                $Moved += $Name
            }
        }
        $HadAppId = Test-Path -LiteralPath "$EldenRingDir\steam_appid.txt"
        if ($HadAppId) { Copy-Item -LiteralPath "$EldenRingDir\steam_appid.txt" -Destination "$ErBackup\steam_appid.txt" }
    }
    New-Item -ItemType Directory -Path "$EldenRingDir\erbridge" -Force | Out-Null
    Copy-Item -LiteralPath "$NativeDist\dinput8.dll" -Destination "$EldenRingDir\dinput8.dll" -Force
    Copy-Item -LiteralPath "$NativeDist\erbridge_core.dll" -Destination "$EldenRingDir\erbridge\erbridge_core.dll" -Force
    # eldenring.exe is started directly (no EAC launcher); this lets it initialise Steam.
    '1245620' | Set-Content -LiteralPath "$EldenRingDir\steam_appid.txt" -Encoding ASCII
    $Record.eldenring = [ordered]@{
        game_dir = $EldenRingDir
        backup = $ErBackup
        moved_mods = $Moved
        original_screen_mode = $OrigScreenMode
        had_steam_appid = $HadAppId
        native_sha256 = (Get-FileHash -LiteralPath "$EldenRingDir\erbridge\erbridge_core.dll").Hash
    }
    Write-Output "Elden Ring: installed. Previous mod files moved: $($Moved.Count) (backup: $ErBackup)."
    foreach ($Name in $Moved) { Write-Output "  moved out of the game folder: $Name (Restore.ps1 puts it back)" }
    Write-Output 'Note: Launch.ps1 switches Elden Ring from FULLSCREEN to BORDERLESS in GraphicsConfig.xml (needed so it does not minimise when ULTRAKILL takes focus). Restore.ps1 offers to undo that.'
}

# ---------------------------------------------------------------- ULTRAKILL (guest, via the kit)
if (-not $SkipUltrakill) {
    # Installation made by UltraRing 0.2.0 (backup + backed_up in installation.json, no kit record): hand it to the kit
    # as its own record so the original backups are kept and Uninstall-Guest.ps1 restores them.
    if ($PrevUk -and -not $PrevUk.guest_record -and -not (Test-Path -LiteralPath $KitRecord)) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $KitRecord) -Force | Out-Null
        ([ordered]@{
            installed_at = (Get-Date).ToString('o')
            game_dir = $UltrakillDir
            guest_dir = $UkStage
            backup = [string]$PrevUk.backup
            backed_up = @($PrevUk.backed_up)
            created_steam_appid = [bool]$PrevUk.created_steam_appid
        } | ConvertTo-Json -Depth 4) | Set-Content -LiteralPath $KitRecord -Encoding UTF8
        Write-Output 'ULTRAKILL: migrated the 0.2.0 installation record to the kit.'
    }
    $KitArgs = @{ UltrakillDir = $UltrakillDir; GuestDir = $UkStage }
    if ($UltrakillSteamAppId) { $KitArgs.SteamAppId = $true }
    & $KitInstall @KitArgs
    $Guest = $null
    if (Test-Path -LiteralPath $KitRecord) { $Guest = Get-Content -LiteralPath $KitRecord -Raw | ConvertFrom-Json }
    if (-not $Guest) { throw "Install-Guest.ps1 did not write $KitRecord." }
    $Record.ultrakill = [ordered]@{
        game_dir = $UltrakillDir
        guest_record = $KitRecord
        backup = [string]$Guest.backup
        backed_up = @($Guest.backed_up)
        created_steam_appid = [bool]$Guest.created_steam_appid
        staged_runtime = $UkStage
    }
}

($Record | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath $RecordPath -Encoding UTF8
Write-Output "Done. Record: $RecordPath. Saves were only copied, never changed."
