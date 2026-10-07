<#
.SYNOPSIS
  Installs the UltraRing bridge into the Elden Ring and ULTRAKILL game folders (run tools\Build.ps1 first).
.DESCRIPTION
  Elden Ring (host), like Minecraft Ring's Install.ps1:
    - refuses to run while eldenring.exe is running
    - backs up %APPDATA%\EldenRing (saves) and moves conflicting mod files out of the game folder into backups\<timestamp>
    - copies dinput8.dll and erbridge\erbridge_core.dll, writes steam_appid.txt (1245620)
  ULTRAKILL (guest):
    - copies BepInEx's winhttp.dll (Doorstop proxy) and a doorstop_config.ini with enabled=false next to ULTRAKILL.exe,
      so a normal Steam launch stays vanilla. Launch.ps1 enables BepInEx for its own launch with
      --doorstop-enabled true --doorstop-target-assembly <repo>\runtime\ultrakill-bepinex\BepInEx\core\BepInEx.Preloader.dll
      so no BepInEx folder, plugin or config is ever copied into the game folder.
    - backs up an existing winhttp.dll / doorstop_config.ini first.
  Everything is recorded in installation.json (used by Launch.ps1 and Restore.ps1). Running Install.ps1 again for
  the same folders only refreshes the files; the original backups are kept.
.PARAMETER EldenRingDir
  The folder containing eldenring.exe (default: the Steam path).
.PARAMETER UltrakillDir
  The folder containing ULTRAKILL.exe (default: the Steam path).
.PARAMETER UltrakillSteamAppId
  Also write steam_appid.txt (1229490) next to ULTRAKILL.exe. Off by default: ULTRAKILL uses Facepunch.Steamworks and
  never calls RestartAppIfNecessary, so starting ULTRAKILL.exe directly (Steam running) works without it. Use this
  only if Launch.ps1's ULTRAKILL exits at once or "restarts through Steam" (which would lose the BepInEx arguments).
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
$RecordPath = Join-Path $ProjectRoot 'installation.json'

function Resolve-Dir([string]$Path) { [IO.Path]::GetFullPath($Path).TrimEnd('\') }
function Assert-Inside([string]$Dir, [string]$Candidate) {
    if (-not $Candidate.StartsWith($Dir + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Invalid path outside the game folder: $Candidate" }
}

# ---------------------------------------------------------------- checks
if ($SkipEldenRing -and $SkipUltrakill) { throw 'Nothing to install.' }
$NativeDist = Join-Path $ProjectRoot 'external\minecraft-ring\dist'
$UkStage = Join-Path $ProjectRoot 'runtime\ultrakill-bepinex'
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
    if (-not (Test-Path -LiteralPath "$UkStage\winhttp.dll") -or -not (Test-Path -LiteralPath "$UkStage\BepInEx\core\BepInEx.Preloader.dll")) {
        throw "The BepInEx runtime is not staged at $UkStage. Run tools\Build.ps1 first."
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
        Write-Output 'Elden Ring: already installed here, refreshing the bridge files.'
    } else {
        $ErBackup = Join-Path $BackupRoot 'eldenring'
        New-Item -ItemType Directory -Path $ErBackup -Force | Out-Null
        $SaveRoot = Join-Path $env:APPDATA 'EldenRing'
        if (Test-Path -LiteralPath $SaveRoot) { Copy-Item -LiteralPath $SaveRoot -Destination "$ErBackup\saves" -Recurse }
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
        had_steam_appid = $HadAppId
        native_sha256 = (Get-FileHash -LiteralPath "$EldenRingDir\erbridge\erbridge_core.dll").Hash
    }
    Write-Output "Elden Ring: installed. Previous mod files moved: $($Moved.Count) (backup: $ErBackup)."
}

# ---------------------------------------------------------------- ULTRAKILL (guest)
if (-not $SkipUltrakill) {
    if ($PrevUk) {
        $UkBackup = [string]$PrevUk.backup
        $UkBackedUp = @($PrevUk.backed_up)
        $UkCreatedAppId = [bool]$PrevUk.created_steam_appid
        Write-Output 'ULTRAKILL: already installed here, refreshing the bridge files.'
    } else {
        $UkBackup = Join-Path $BackupRoot 'ultrakill'
        New-Item -ItemType Directory -Path $UkBackup -Force | Out-Null
        $UkBackedUp = @()
        foreach ($Name in 'winhttp.dll', 'doorstop_config.ini') {
            $Candidate = [IO.Path]::GetFullPath((Join-Path $UltrakillDir $Name))
            Assert-Inside $UltrakillDir $Candidate
            if (Test-Path -LiteralPath $Candidate) {
                Move-Item -LiteralPath $Candidate -Destination (Join-Path $UkBackup $Name)
                $UkBackedUp += $Name
            }
        }
        $UkCreatedAppId = $false
    }
    Copy-Item -LiteralPath "$UkStage\winhttp.dll" -Destination "$UltrakillDir\winhttp.dll" -Force
    # Doorstop reads this next to the exe. enabled=false: a normal Steam launch stays vanilla; Launch.ps1 passes
    # --doorstop-enabled true --doorstop-target-assembly <path> to switch BepInEx on for its own launch only.
    $Ini = @(
        '# UltraRing: BepInEx is OFF for normal launches. Launch.ps1 enables it on the command line.',
        '[General]',
        'enabled = false',
        'target_assembly = BepInEx\core\BepInEx.Preloader.dll',
        'redirect_output_log = false',
        'boot_config_override =',
        'ignore_disable_switch = false',
        '',
        '[UnityMono]',
        'dll_search_path_override =',
        'debug_enabled = false',
        'debug_address = 127.0.0.1:10000',
        'debug_suspend = false'
    )
    Set-Content -LiteralPath "$UltrakillDir\doorstop_config.ini" -Value $Ini -Encoding ASCII
    if ($UltrakillSteamAppId) {
        $AppIdPath = "$UltrakillDir\steam_appid.txt"
        if (Test-Path -LiteralPath $AppIdPath) {
            Write-Output 'ULTRAKILL: steam_appid.txt already exists; left alone.'
        } else {
            '1229490' | Set-Content -LiteralPath $AppIdPath -Encoding ASCII
            $UkCreatedAppId = $true
        }
    }
    $Record.ultrakill = [ordered]@{
        game_dir = $UltrakillDir
        backup = $UkBackup
        backed_up = $UkBackedUp
        created_steam_appid = $UkCreatedAppId
        staged_runtime = $UkStage
    }
    Write-Output "ULTRAKILL: installed (BepInEx disabled by default; Launch.ps1 enables it). Backed up: $($UkBackedUp.Count) file(s)."
}

($Record | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath $RecordPath -Encoding UTF8
Write-Output "Done. Record: $RecordPath. Saves were only copied, never changed."
