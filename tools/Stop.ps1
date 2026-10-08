<#
.SYNOPSIS
  Stops ULTRAKILL and releases Elden Ring: closes ULTRAKILL (gracefully first, then forced) and clears the bridge
  control block so the host stops compositing the guest's last frame and gives the camera/character back.
.PARAMETER All
  Also close Elden Ring (and the FakeHost) gracefully. Never forced: if it does not close, close it yourself.
.PARAMETER GraceSeconds
  How long to wait for ULTRAKILL to close by itself before it is killed (default 5).
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$All,
    [int]$GraceSeconds = 5
)
$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'BridgeCommon.ps1')
$SharedFile = Join-Path $ProjectRoot 'runtime\bridge.shm'

# ---- ULTRAKILL
$Uk = @(Get-Process ULTRAKILL -ErrorAction SilentlyContinue)
if ($Uk.Count -eq 0) { Write-Output 'ULTRAKILL: not running.' }
foreach ($P in $Uk) {
    if (-not $PSCmdlet.ShouldProcess("ULTRAKILL (PID $($P.Id))", 'close')) { continue }
    Write-Output "ULTRAKILL (PID $($P.Id)): asking it to close..."
    if (Close-ProcessGracefully $P $GraceSeconds) { Write-Output 'ULTRAKILL: closed.'; continue }
    Write-Output "ULTRAKILL: still running after $GraceSeconds s, killing it."
    try { Stop-Process -Id $P.Id -Force -ErrorAction Stop } catch { Write-Warning "Could not kill PID $($P.Id): $($_.Exception.Message)" }
    if (-not $P.WaitForExit(5000)) { throw "ULTRAKILL (PID $($P.Id)) did not exit; the control block was left as it is." }
}

# ---- control block
$State = Read-BridgeHeader $SharedFile
if (-not $State) {
    Write-Output 'Bridge: no bridge.shm (or not initialised); nothing to clear.'
} elseif ($PSCmdlet.ShouldProcess($SharedFile, 'clear the control block')) {
    if (Clear-BridgeControl $SharedFile) {
        Write-Output ('Bridge: control block cleared (flags were 0x{0:X}). Elden Ring has its camera and character back.' -f $State.ControlFlags)
    } else {
        Write-Warning 'Bridge: could not clear the control block.'
    }
}

# ---- hosts
if ($All) {
    foreach ($Name in 'eldenring', 'UltraRing.FakeHost') {
        foreach ($P in @(Get-Process $Name -ErrorAction SilentlyContinue)) {
            if (-not $PSCmdlet.ShouldProcess("$Name (PID $($P.Id))", 'close')) { continue }
            Write-Output "${Name} (PID $($P.Id)): asking it to close..."
            if (Close-ProcessGracefully $P 20) { Write-Output "${Name}: closed." }
            else { Write-Warning "$Name did not close by itself; close it manually (it is not forced)." }
        }
    }
}
Write-Output 'Done.'
