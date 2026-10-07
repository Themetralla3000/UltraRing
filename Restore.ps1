<#
.SYNOPSIS
  Undoes Install.ps1: moves the bridge files out of the game folders and puts back what Install moved away.
  Save backups are never applied and nothing in %APPDATA%\EldenRing is touched.
#>
$ErrorActionPreference = 'Stop'
$ProjectRoot = $PSScriptRoot
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
    }
}
function Put-Back([string]$GameDir, [string]$BackupDir, [string]$Name) {
    $Source = Join-Path $BackupDir $Name
    if (Test-Path -LiteralPath $Source) {
        Copy-Item -LiteralPath $Source -Destination (Join-Path $GameDir $Name) -Recurse -Force
    }
}

if ($Install.eldenring -and (Get-Process eldenring -ErrorAction SilentlyContinue)) { throw 'Close Elden Ring first.' }
if ($Install.ultrakill -and (Get-Process ULTRAKILL -ErrorAction SilentlyContinue)) { throw 'Close ULTRAKILL first.' }
New-Item -ItemType Directory -Path $Archive -Force | Out-Null

# ---------------------------------------------------------------- Elden Ring
if ($Install.eldenring) {
    $Er = $Install.eldenring
    $To = Join-Path $Archive 'eldenring'
    foreach ($Name in 'dinput8.dll', 'erbridge', 'steam_appid.txt') { Move-Out $Er.game_dir $Name $To }
    foreach ($Name in @($Er.moved_mods)) { Put-Back $Er.game_dir $Er.backup $Name }
    if ($Er.had_steam_appid) { Put-Back $Er.game_dir $Er.backup 'steam_appid.txt' }
    Write-Output 'Elden Ring: bridge removed, previous mod files restored.'
}

# ---------------------------------------------------------------- ULTRAKILL
if ($Install.ultrakill) {
    $Uk = $Install.ultrakill
    $To = Join-Path $Archive 'ultrakill'
    foreach ($Name in 'winhttp.dll', 'doorstop_config.ini') { Move-Out $Uk.game_dir $Name $To }
    if ($Uk.created_steam_appid) { Move-Out $Uk.game_dir 'steam_appid.txt' $To }
    foreach ($Name in @($Uk.backed_up)) { Put-Back $Uk.game_dir $Uk.backup $Name }
    Write-Output 'ULTRAKILL: BepInEx proxy removed, previous files restored.'
}

Move-Item -LiteralPath $RecordPath -Destination (Join-Path $Archive 'installation.json')
Write-Output "The bridge is disabled. Removed files are in $Archive. Save backups have not been applied."
