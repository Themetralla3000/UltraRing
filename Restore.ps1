<#
.SYNOPSIS
  Undoes Install.ps1: moves the bridge files out of the game folders and puts back what Install moved away
  (e.g. Seamless Coop: the SeamlessCoop folder and ersc_launcher.exe). Saves are never overwritten.
.DESCRIPTION
  - Files/folders Install moved into backups\<timestamp>\eldenring are COPIED back (the backup stays untouched, so the
    ORIGINAL backup survives repeated installs/restores). A target that already exists in the game folder is moved to
    the archive folder first, never deleted.
  - Optionally puts Elden Ring's original ScreenMode back in %APPDATA%\EldenRing\GraphicsConfig.xml (only that
    element, read from the backup copy taken by Install.ps1; nothing else in the saves folder is touched).
.PARAMETER ScreenMode
  Ask (default; prompts when interactive, otherwise keeps the current value), Restore, or Keep.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Ask', 'Restore', 'Keep')][string]$ScreenMode = 'Ask'
)
$ErrorActionPreference = 'Stop'
$ProjectRoot = $PSScriptRoot
. (Join-Path $PSScriptRoot 'tools\BridgeCommon.ps1')
$RecordPath = Join-Path $ProjectRoot 'installation.json'
if (-not (Test-Path -LiteralPath $RecordPath)) { throw 'installation.json was not found; nothing to restore.' }
$Install = Get-Content -LiteralPath $RecordPath -Raw | ConvertFrom-Json
$Stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$Archive = Join-Path $ProjectRoot "backups\bridge-disabled-$Stamp"

function Assert-Inside([string]$Dir, [string]$Candidate) {
    if (-not $Candidate.StartsWith($Dir.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Invalid restore path: $Candidate" }
}
function Move-Out([string]$GameDir, [string]$Name, [string]$To) {
    $Candidate = [IO.Path]::GetFullPath((Join-Path $GameDir $Name))
    Assert-Inside $GameDir $Candidate
    if (Test-Path -LiteralPath $Candidate) {
        New-Item -ItemType Directory -Path $To -Force | Out-Null
        Move-Item -LiteralPath $Candidate -Destination (Join-Path $To $Name)
        Write-Output "  removed from the game folder: $Name (kept in $To)"
    }
}
function Put-Back([string]$GameDir, [string]$BackupDir, [string]$Name, [string]$ConflictDir) {
    $Source = Join-Path $BackupDir $Name
    if (-not (Test-Path -LiteralPath $Source)) { Write-Warning "  not in the backup, cannot restore: $Name ($BackupDir)"; return }
    $Target = [IO.Path]::GetFullPath((Join-Path $GameDir $Name))
    Assert-Inside $GameDir $Target
    if (Test-Path -LiteralPath $Target) {
        # Something is already there (e.g. the user reinstalled the mod): set it aside, do not merge into it.
        New-Item -ItemType Directory -Path $ConflictDir -Force | Out-Null
        Move-Item -LiteralPath $Target -Destination (Join-Path $ConflictDir $Name)
        Write-Output "  already existed, moved aside: $Name (to $ConflictDir)"
    }
    # Copy, not move: the original backup stays complete. The target no longer exists, so a folder is copied as that folder.
    Copy-Item -LiteralPath $Source -Destination $Target -Recurse
    $Kind = if (Test-Path -LiteralPath $Source -PathType Container) { 'folder' } else { 'file' }
    Write-Output "  restored $Kind`: $Name"
}

if ($Install.eldenring -and (Get-Process eldenring -ErrorAction SilentlyContinue)) { throw 'Close Elden Ring first (Stop.bat -All).' }
if ($Install.ultrakill -and (Get-Process ULTRAKILL -ErrorAction SilentlyContinue)) { throw 'Close ULTRAKILL first (Stop.bat).' }
if ($Install.eldenring -and -not (Test-Path -LiteralPath ([string]$Install.eldenring.backup))) {
    throw "The Elden Ring backup folder is missing: $($Install.eldenring.backup). Nothing was changed."
}
if ($Install.ultrakill -and -not (Test-Path -LiteralPath ([string]$Install.ultrakill.backup))) {
    throw "The ULTRAKILL backup folder is missing: $($Install.ultrakill.backup). Nothing was changed."
}
New-Item -ItemType Directory -Path $Archive -Force | Out-Null

# ---------------------------------------------------------------- Elden Ring
if ($Install.eldenring) {
    $Er = $Install.eldenring
    Write-Output "Elden Ring ($($Er.game_dir)):"
    $To = Join-Path $Archive 'eldenring'
    foreach ($Name in 'dinput8.dll', 'erbridge', 'steam_appid.txt') { Move-Out $Er.game_dir $Name $To }
    $Moved = @($Er.moved_mods | Where-Object { $_ })
    foreach ($Name in $Moved) { Put-Back $Er.game_dir $Er.backup $Name (Join-Path $Archive 'eldenring-conflicts') }
    if ($Er.had_steam_appid) { Put-Back $Er.game_dir $Er.backup 'steam_appid.txt' (Join-Path $Archive 'eldenring-conflicts') }
    Write-Output "Elden Ring: bridge removed; $($Moved.Count) previous item(s) from the original backup put back (backup kept: $($Er.backup))."

    # Screen mode: only the <ScreenMode> element of GraphicsConfig.xml, value taken from the backup copy.
    $Config = Join-Path $env:APPDATA 'EldenRing\GraphicsConfig.xml'
    $Original = $null
    try { $Original = Get-ScreenMode (Join-Path $Er.backup 'saves\GraphicsConfig.xml') } catch { }
    if (-not $Original -and $Er.original_screen_mode) { $Original = [string]$Er.original_screen_mode }
    $Current = $null
    try { $Current = Get-ScreenMode $Config } catch { }
    if ($Original -and $Current -and $Original -ne $Current) {
        Write-Output "Note: Launch.ps1 changed Elden Ring's ScreenMode from $Original to $Current (the input overlay needs it)."
        $Do = $false
        if ($ScreenMode -eq 'Restore') { $Do = $true }
        elseif ($ScreenMode -eq 'Ask' -and -not [Console]::IsInputRedirected) {
            $Do = (Read-Host "Put ScreenMode back to $Original ? [y/N]") -match '^(y|yes|s|si)$'
        } elseif ($ScreenMode -eq 'Ask') {
            Write-Output "Not asking (no console input); keeping $Current. Run Restore.ps1 -ScreenMode Restore to change it."
        }
        if ($Do -and $PSCmdlet.ShouldProcess($Config, "set ScreenMode to $Original")) {
            [void](Set-ScreenMode $Config $Original)
            Write-Output "Elden Ring ScreenMode restored to $Original (only that element was changed; saves untouched)."
        } elseif (-not $Do) {
            Write-Output "ScreenMode left at $Current."
        }
    }
}

# ---------------------------------------------------------------- ULTRAKILL
if ($Install.ultrakill) {
    $Uk = $Install.ultrakill
    Write-Output "ULTRAKILL ($($Uk.game_dir)):"
    $KitUninstall = Join-Path $ProjectRoot 'bridge\scripts\Uninstall-Guest.ps1'
    if ($Uk.guest_record -and (Test-Path -LiteralPath ([string]$Uk.guest_record)) -and (Test-Path -LiteralPath $KitUninstall)) {
        # Installed through the kit (UltraRing 0.3.0+): let it undo its own install.
        if ($WhatIfPreference) { & $KitUninstall -UltrakillDir $Uk.game_dir -WhatIf } else { & $KitUninstall -UltrakillDir $Uk.game_dir }
    } else {
        # UltraRing 0.2.0 record (or the kit record is gone): same steps as before.
        $To = Join-Path $Archive 'ultrakill'
        foreach ($Name in 'winhttp.dll', 'doorstop_config.ini') { Move-Out $Uk.game_dir $Name $To }
        if ($Uk.created_steam_appid) { Move-Out $Uk.game_dir 'steam_appid.txt' $To }
        foreach ($Name in @($Uk.backed_up | Where-Object { $_ })) { Put-Back $Uk.game_dir $Uk.backup $Name (Join-Path $Archive 'ultrakill-conflicts') }
        Write-Output 'ULTRAKILL: BepInEx proxy removed, previous files restored.'
    }
}

if ($WhatIfPreference) { Write-Output 'What if: no changes were made.'; return }
Move-Item -LiteralPath $RecordPath -Destination (Join-Path $Archive 'installation.json')
Write-Output "The bridge is disabled. Removed files are in $Archive. Save backups have not been applied."
