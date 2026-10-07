<#
.SYNOPSIS
  Starts the bridge: the host (Elden Ring, or the FakeHost test tool) first, then ULTRAKILL with BepInEx.
.PARAMETER FakeHost
  Start tools\UltraRing.FakeHost (a fake Elden Ring on the same protocol) instead of Elden Ring. Needs no Elden Ring
  installation; only the ULTRAKILL side of Install.ps1.
.PARAMETER HostOnly
  Start (and wait for) the host only; do not start ULTRAKILL.
#>
param(
    [switch]$FakeHost,
    [switch]$HostOnly
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = $PSScriptRoot
$RecordPath = Join-Path $ProjectRoot 'installation.json'
if (-not (Test-Path -LiteralPath $RecordPath)) { throw 'The bridge is not installed. Run Install.ps1 first (after tools\Build.ps1).' }
$Install = Get-Content -LiteralPath $RecordPath -Raw | ConvertFrom-Json

$RuntimeDir = Join-Path $ProjectRoot 'runtime'
$UkStage = Join-Path $RuntimeDir 'ultrakill-bepinex'
$Preloader = Join-Path $UkStage 'BepInEx\core\BepInEx.Preloader.dll'
$Plugin = Join-Path $UkStage 'BepInEx\plugins\UltraRing\UltraRing.Ultrakill.dll'
if (-not $HostOnly) {
    if (-not $Install.ultrakill) { throw 'ULTRAKILL is not installed (run Install.ps1 without -SkipUltrakill).' }
    $UkExe = Join-Path $Install.ultrakill.game_dir 'ULTRAKILL.exe'
    if (-not (Test-Path -LiteralPath $UkExe)) { throw "ULTRAKILL was not found: $UkExe" }
    if (-not (Test-Path -LiteralPath $Preloader) -or -not (Test-Path -LiteralPath $Plugin)) {
        throw "The staged BepInEx runtime is incomplete ($UkStage). Run tools\Build.ps1."
    }
}

# Both sides must use the same bridge folder; the host DLL only loads with ERBRIDGE=1.
$env:ERMC_DIR = $RuntimeDir
$env:ERBRIDGE = '1'
New-Item -ItemType Directory -Path $RuntimeDir -Force | Out-Null
$SharedFile = Join-Path $RuntimeDir 'bridge.shm'

function Read-BridgeHeader {
    if (-not (Test-Path -LiteralPath $SharedFile)) { return $null }
    try {
        $Header = New-Object byte[] 128
        $Stream = [IO.File]::Open($SharedFile, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try { $Read = $Stream.Read($Header, 0, $Header.Length) } finally { $Stream.Dispose() }
        if ($Read -ne $Header.Length -or [BitConverter]::ToUInt32($Header, 0) -ne 0x434D484D) { return $null }
        return [PSCustomObject]@{
            HostProcessId = [BitConverter]::ToUInt32($Header, 0x20)
            GuestProcessId = [BitConverter]::ToUInt32($Header, 0x24)
            GuestStartMs = [BitConverter]::ToUInt64($Header, 0x30)
            CoreStatus = [BitConverter]::ToInt32($Header, 0x44)
        }
    } catch { return $null }
}

# ---------------------------------------------------------------- host
if ($FakeHost) {
    $HostName = 'UltraRing.FakeHost'
    $FakeExe = Join-Path $ProjectRoot 'tools\UltraRing.FakeHost\bin\Release\net8.0-windows\UltraRing.FakeHost.exe'
    $HostProcess = Get-Process $HostName -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $HostProcess) {
        if (-not (Test-Path -LiteralPath $FakeExe)) { throw "FakeHost is not built ($FakeExe). Run tools\Build.ps1." }
        Write-Output 'Starting the FakeHost (fake Elden Ring)...'
        $HostProcess = Start-Process -FilePath $FakeExe -ArgumentList @('--dir', ('"' + $RuntimeDir + '"')) -WorkingDirectory (Split-Path -Parent $FakeExe) -PassThru
    }
} else {
    if (-not $Install.eldenring) { throw 'Elden Ring is not installed (run Install.ps1 without -SkipEldenRing), or use -FakeHost.' }
    $GameExe = Join-Path $Install.eldenring.game_dir 'eldenring.exe'
    if (-not (Test-Path -LiteralPath $GameExe)) { throw "Elden Ring was not found: $GameExe" }
    if (-not (Get-Process steam -ErrorAction SilentlyContinue)) { Write-Warning 'Steam is not running; Elden Ring needs the Steam client (offline mode is fine).' }
    $HostProcess = Get-Process eldenring -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $GameExe } | Select-Object -First 1
    if (-not $HostProcess) {
        Write-Output 'Starting Elden Ring...'
        $HostProcess = Start-Process -FilePath $GameExe -WorkingDirectory $Install.eldenring.game_dir -PassThru
    }
}

Write-Output 'Waiting for the host to publish the bridge...'
$Watch = [Diagnostics.Stopwatch]::StartNew()
$Ready = $false
while ($Watch.Elapsed.TotalSeconds -lt 90) {
    $State = Read-BridgeHeader
    if ($State -and $State.HostProcessId -eq $HostProcess.Id) {
        if ($State.CoreStatus -eq 1) { $Ready = $true; break }
        if ($State.CoreStatus -lt 0) { throw "The host bridge failed to load (status $($State.CoreStatus)). See runtime\er-bridge.log." }
    }
    if (-not (Get-Process -Id $HostProcess.Id -ErrorAction SilentlyContinue)) {
        throw 'The host closed during startup. ULTRAKILL was not launched. See runtime\er-bridge.log (or runtime\fakehost.log).'
    }
    Start-Sleep -Milliseconds 500
}
if (-not $Ready) { throw 'The host did not load the bridge within 90 s. Close it and start again. See runtime\er-bridge.log.' }
Write-Output "Host bridge ready (PID $($HostProcess.Id))."
if (-not $FakeHost) { Write-Output 'In Elden Ring, choose Continue and load into the world.' }
if ($HostOnly) { return }

# ---------------------------------------------------------------- guard against a second ULTRAKILL guest
$State = Read-BridgeHeader
if ($State -and $State.GuestProcessId -gt 0) {
    $Guest = Get-Process -Id $State.GuestProcessId -ErrorAction SilentlyContinue
    if ($Guest -and $Guest.ProcessName -eq 'ULTRAKILL') {
        $StartedMs = [DateTimeOffset]::new($Guest.StartTime).ToUnixTimeMilliseconds()
        if ([Math]::Abs([double]$StartedMs - [double]$State.GuestStartMs) -lt 120000) {
            Write-Output "ULTRAKILL is already attached to the bridge (PID $($Guest.Id)). Use F8 to switch control."
            return
        }
    }
}
if (Get-Process ULTRAKILL -ErrorAction SilentlyContinue) {
    throw 'ULTRAKILL is already running without the bridge. Close it first (BepInEx can only be enabled at startup).'
}

# ---------------------------------------------------------------- ULTRAKILL with BepInEx
# doorstop_config.ini in the game folder says enabled=false; the command line switches BepInEx on for this launch only.
$UkArgs = '--doorstop-enabled true --doorstop-target-assembly "' + $Preloader + '" -screen-fullscreen 0 -popupwindow'
Write-Output 'Starting ULTRAKILL with BepInEx...'
$UkProcess = Start-Process -FilePath $UkExe -ArgumentList $UkArgs -WorkingDirectory $Install.ultrakill.game_dir -PassThru

$Watch.Restart()
$Attached = $false
while ($Watch.Elapsed.TotalSeconds -lt 60) {
    $State = Read-BridgeHeader
    if ($State -and $State.GuestProcessId -eq $UkProcess.Id) { $Attached = $true; break }
    if (-not (Get-Process -Id $UkProcess.Id -ErrorAction SilentlyContinue)) {
        throw "ULTRAKILL exited during startup. See $UkStage\BepInEx\LogOutput.log and the Unity player log."
    }
    Start-Sleep -Milliseconds 500
}

Write-Output ''
Write-Output "Host      : PID $($HostProcess.Id) $(if ($FakeHost) { '(FakeHost)' } else { '(Elden Ring)' })"
Write-Output "ULTRAKILL : PID $($UkProcess.Id) $(if ($Attached) { '(attached to the bridge)' } else { '(not attached yet; see ' + $UkStage + '\BepInEx\LogOutput.log)' })"
Write-Output "Bridge dir: $RuntimeDir"
Write-Output 'F8 hands control to the host and back. Logs: runtime\er-bridge.log, runtime\ultrakill-bepinex\BepInEx\LogOutput.log'
