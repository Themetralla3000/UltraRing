<#
.SYNOPSIS
  Builds everything UltraRing needs: the Elden Ring host DLLs (from the pinned Minecraft Ring source) and, through the
  ULTRAKILL Crossover Bridge kit (git submodule bridge\), the staged ULTRAKILL guest at bridge\dist\guest.
  Touches no game folder and starts no game.
.PARAMETER Configuration
  dotnet build configuration for the guest (default Release).
.PARAMETER UltrakillDir
  The folder containing ULTRAKILL.exe (its Managed DLLs are needed to compile the guest).
.PARAMETER SkipNative
  Do not (re)build the Elden Ring host DLLs.
.PARAMETER SkipGuest
  Do not build/stage the ULTRAKILL guest.
.PARAMETER SkipManaged
  Guest: do not run dotnet build, only stage what is already built (bridge\scripts\Build.ps1 -SkipBuild).
#>
param(
    [string]$Configuration = 'Release',
    [string]$UltrakillDir = 'C:\Program Files (x86)\Steam\steamapps\common\ULTRAKILL',
    [switch]$SkipNative,
    [switch]$SkipGuest,
    [switch]$SkipManaged
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$Root = Split-Path -Parent $PSScriptRoot
$Tools = Join-Path $Root '.tools'

# Pinned versions (docs/research/toolchain.md)
$McRingUrl = 'https://github.com/siddoff/Minecraft-Ring'
$McRingCommit = '711015afa67ab25c7b9597c68038718d1cef322e'
$LlvmTag = '20261006'
$LlvmName = "llvm-mingw-$LlvmTag-ucrt-x86_64"
$LlvmUrl = "https://github.com/mstorsjo/llvm-mingw/releases/download/$LlvmTag/$LlvmName.zip"
$LlvmSha = '317492c456aa27ee607a5919f1d2d38dcdc1112516a24d0bf4b00d078f52d17a'

function Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }

function Get-Download([string]$Url, [string]$Sha256, [string]$Dest) {
    if (Test-Path -LiteralPath $Dest) {
        if ((Get-FileHash -LiteralPath $Dest -Algorithm SHA256).Hash -ieq $Sha256) { return }
        Remove-Item -LiteralPath $Dest -Force
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $Dest) -Force | Out-Null
    Write-Host "    downloading $Url"
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    try { Invoke-WebRequest -Uri $Url -OutFile $Dest -UseBasicParsing }
    catch { throw "Could not download $Url ($($_.Exception.Message)). Download it manually to '$Dest' and run again." }
    $Actual = (Get-FileHash -LiteralPath $Dest -Algorithm SHA256).Hash
    if ($Actual -ine $Sha256) {
        Remove-Item -LiteralPath $Dest -Force
        throw "SHA-256 mismatch for $Url (expected $Sha256, got $Actual)."
    }
}

# ---------------------------------------------------------------- 1. host source
$McDir = Join-Path $Root 'external\minecraft-ring'
if (-not $SkipNative) {
    Step 'Minecraft Ring checkout (pinned host source)'
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'git was not found in PATH.' }
    if (-not (Test-Path -LiteralPath (Join-Path $McDir '.git'))) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $McDir) -Force | Out-Null
        & git clone $McRingUrl $McDir
        if ($LASTEXITCODE -ne 0) { throw 'git clone of Minecraft Ring failed.' }
    }
    $Head = (& git -C $McDir rev-parse HEAD).Trim()
    if ($Head -ne $McRingCommit) {
        Write-Host "    checking out $McRingCommit (was $Head)"
        & git -C $McDir checkout --quiet $McRingCommit
        if ($LASTEXITCODE -ne 0) { throw "Could not check out $McRingCommit in $McDir." }
    }

    # ---------------------------------------------------------------- 2. compiler
    Step 'LLVM-MinGW compiler'
    $CompilerRoot = Join-Path $Tools 'compiler'
    $Clang = @(Get-ChildItem -Path "$CompilerRoot\*\bin\clang++.exe" -ErrorAction SilentlyContinue)
    if ($Clang.Count -eq 0) {
        $Zip = Join-Path $Tools "downloads\llvm-mingw\$LlvmName.zip"
        Get-Download $LlvmUrl $LlvmSha $Zip
        New-Item -ItemType Directory -Path $CompilerRoot -Force | Out-Null
        Write-Host '    extracting'
        Expand-Archive -LiteralPath $Zip -DestinationPath $CompilerRoot -Force
        $Clang = @(Get-ChildItem -Path "$CompilerRoot\*\bin\clang++.exe" -ErrorAction SilentlyContinue)
        if ($Clang.Count -eq 0) { throw "clang++.exe not found after extracting $Zip." }
    }
    if ($Clang.Count -gt 1) { Write-Warning 'More than one compiler folder in .tools\compiler; build_native.py uses the first match.' }

    # build_native.py looks for <its repo>\.tools\compiler\*\bin\clang++.exe: point it at ours with a junction.
    $Junction = Join-Path $McDir '.tools\compiler'
    if (-not (Test-Path -LiteralPath $Junction)) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $Junction) -Force | Out-Null
        New-Item -ItemType Junction -Path $Junction -Target $CompilerRoot | Out-Null
        Write-Host "    junction $Junction -> $CompilerRoot"
    }

    # ---------------------------------------------------------------- 3. native build
    Step 'Host DLLs (dinput8.dll, erbridge_core.dll)'
    $Python = Get-Command python -ErrorAction SilentlyContinue
    if (-not $Python) { $Python = Get-Command py -ErrorAction SilentlyContinue }
    if (-not $Python) { throw 'Python 3 was not found in PATH (needed for build_native.py).' }
    Push-Location $McDir
    try {
        & $Python.Source 'tools\build_native.py'
        if ($LASTEXITCODE -ne 0) { throw 'build_native.py failed.' }
    } finally { Pop-Location }
    foreach ($Name in 'dinput8.dll', 'erbridge_core.dll') {
        if (-not (Test-Path -LiteralPath (Join-Path $McDir "dist\$Name"))) { throw "Native build did not produce dist\$Name." }
    }
}

# ---------------------------------------------------------------- 4. ULTRAKILL guest (the kit)
$Kit = Join-Path $Root 'bridge'
$KitBuild = Join-Path $Kit 'scripts\Build.ps1'
$GuestStage = Join-Path $Kit 'dist\guest'
if (-not $SkipGuest) {
    if (-not (Test-Path -LiteralPath $KitBuild)) {
        throw 'The ULTRAKILL Crossover Bridge submodule (bridge\) is not initialised. Run: git submodule update --init'
    }
    Step 'ULTRAKILL guest (bridge\scripts\Build.ps1)'
    $KitArgs = @{ Configuration = $Configuration; UltrakillDir = $UltrakillDir }
    if ($SkipManaged) { $KitArgs.SkipBuild = $true }
    & $KitBuild @KitArgs

    # One-time migration of the v0.2.0 plugin config (renamed with the split into the kit).
    $OldCfg = Join-Path $Root 'runtime\ultrakill-bepinex\BepInEx\config\dev.ultraring.ultrakill.cfg'
    $NewCfg = Join-Path $GuestStage 'BepInEx\config\dev.ukbridge.guest.cfg'
    if ((Test-Path -LiteralPath $OldCfg) -and -not (Test-Path -LiteralPath $NewCfg)) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $NewCfg) -Force | Out-Null
        Copy-Item -LiteralPath $OldCfg -Destination $NewCfg
        Write-Host "    migrated your old plugin config to $NewCfg" -ForegroundColor Yellow
        Write-Host '    (runtime\ultrakill-bepinex is no longer used; delete it once you have checked the new config)' -ForegroundColor Yellow
    }
}

Write-Host ''
Write-Host 'Build finished.' -ForegroundColor Green
Write-Host "  host DLLs    : $McDir\dist"
Write-Host "  staged guest : $GuestStage"
Write-Host 'Next: .\Install.ps1, then .\Launch.ps1 (or .\Launch.ps1 -FakeHost to test without Elden Ring).'
