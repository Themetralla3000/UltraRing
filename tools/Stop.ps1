<#
.SYNOPSIS
  Stops ULTRAKILL and releases Elden Ring: delegates to the kit's bridge\scripts\Stop-Guest.ps1 (closes ULTRAKILL,
  gracefully first then forced, and clears the bridge control block so the host gives the camera/character back).
.PARAMETER All
  Also close Elden Ring (and the fake host) gracefully. Never forced: if it does not close, close it yourself.
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
$KitStop = Join-Path $ProjectRoot 'bridge\scripts\Stop-Guest.ps1'
if (-not (Test-Path -LiteralPath $KitStop)) {
    throw 'The ULTRAKILL Crossover Bridge submodule (bridge\) is not initialised. Run: git submodule update --init'
}
$KitArgs = @{ BridgeDir = (Join-Path $ProjectRoot 'runtime'); GraceSeconds = $GraceSeconds }
if ($All) { $KitArgs.All = $true; $KitArgs.HostProcessName = @('eldenring') }
if ($WhatIfPreference) { $KitArgs.WhatIf = $true }
& $KitStop @KitArgs
