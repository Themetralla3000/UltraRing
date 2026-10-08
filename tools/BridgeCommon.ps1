# Shared helpers for Launch.ps1, Stop.ps1, Focus.ps1 and Restore.ps1 (dot-sourced; PowerShell 5.1 compatible).
# Bridge file layout: docs/research/host-contract.md (header 0x0, control block 0x800 size 0x64, seqlock).

$script:CtrlOffset = 0x800
$script:CtrlSize = 0x64

if (-not ('UrWin' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class UrWin {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumThreadWindows(uint tid, EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern void keybd_event(byte k, byte s, uint f, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);

  // All top-level windows (visible or not, owned or not) of the given threads.
  public static IntPtr[] ThreadWindows(int[] tids) {
    List<IntPtr> list = new List<IntPtr>();
    foreach (int t in tids) { EnumThreadWindows((uint)t, delegate(IntPtr h, IntPtr l) { list.Add(h); return true; }, IntPtr.Zero); }
    return list.ToArray();
  }
  public static string ClassOf(IntPtr h) { StringBuilder s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
  public static string TitleOf(IntPtr h) { StringBuilder s = new StringBuilder(256); GetWindowText(h, s, 256); return s.ToString(); }
}
'@
}

function Get-ProcessWindows([System.Diagnostics.Process]$Process) {
    # Top-level windows of every thread of the process, including invisible and owned ones (MainWindowHandle misses those).
    $Process.Refresh()
    $Tids = @($Process.Threads | ForEach-Object { [int]$_.Id })
    $Result = @()
    foreach ($H in [UrWin]::ThreadWindows($Tids)) {
        $R = New-Object UrWin+RECT
        [void][UrWin]::GetWindowRect($H, [ref]$R)
        $Result += [PSCustomObject]@{
            Handle = $H
            Class = [UrWin]::ClassOf($H)
            Title = [UrWin]::TitleOf($H)
            Visible = [UrWin]::IsWindowVisible($H)
            Iconic = [UrWin]::IsIconic($H)
            Left = $R.Left; Top = $R.Top; Width = $R.Right - $R.Left; Height = $R.Bottom - $R.Top
        }
    }
    return $Result
}

function Read-BridgeHeader([string]$SharedFile) {
    if (-not (Test-Path -LiteralPath $SharedFile)) { return $null }
    try {
        $Size = $script:CtrlOffset + $script:CtrlSize
        $Buf = New-Object byte[] $Size
        $Stream = [IO.File]::Open($SharedFile, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try { $Read = $Stream.Read($Buf, 0, $Size) } finally { $Stream.Dispose() }
        if ($Read -ne $Size -or [BitConverter]::ToUInt32($Buf, 0) -ne 0x434D484D) { return $null }
        return [PSCustomObject]@{
            HostProcessId = [BitConverter]::ToUInt32($Buf, 0x20)
            GuestProcessId = [BitConverter]::ToUInt32($Buf, 0x24)
            GuestStartMs = [BitConverter]::ToUInt64($Buf, 0x30)
            CoreStatus = [BitConverter]::ToInt32($Buf, 0x44)
            ControlSeq = [BitConverter]::ToUInt32($Buf, $script:CtrlOffset)
            ControlFlags = [BitConverter]::ToUInt32($Buf, $script:CtrlOffset + 4)
        }
    } catch { return $null }
}

function Clear-BridgeControl([string]$SharedFile) {
    # Seqlock write: seq -> even, seq+1 (odd), zero 0x804..0x863, seq+2 (even, never 0). Releases camera/character to the host.
    if (-not (Test-Path -LiteralPath $SharedFile)) { return $false }
    if ($WhatIfPreference) { Write-Output 'What if: clearing the bridge control block.'; return $false }
    $Stream = [IO.File]::Open($SharedFile, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::ReadWrite)
    try {
        $Magic = New-Object byte[] 4
        [void]$Stream.Read($Magic, 0, 4)
        if ([BitConverter]::ToUInt32($Magic, 0) -ne 0x434D484D) { return $false }
        $Stream.Position = $script:CtrlOffset
        $SeqBytes = New-Object byte[] 4
        [void]$Stream.Read($SeqBytes, 0, 4)
        [uint64]$Seq = [BitConverter]::ToUInt32($SeqBytes, 0)
        if (($Seq % 2) -eq 1) {
            $Seq = ($Seq + 1) % 4294967296
            $Stream.Position = $script:CtrlOffset
            $Stream.Write([BitConverter]::GetBytes([uint32]$Seq), 0, 4); $Stream.Flush()
        }
        $Stream.Position = $script:CtrlOffset
        $Stream.Write([BitConverter]::GetBytes([uint32](($Seq + 1) % 4294967296)), 0, 4); $Stream.Flush()
        $Stream.Position = $script:CtrlOffset + 4
        $Stream.Write((New-Object byte[] ($script:CtrlSize - 4)), 0, $script:CtrlSize - 4); $Stream.Flush()
        [uint64]$Final = ($Seq + 2) % 4294967296
        if ($Final -eq 0) { $Final = 2 }
        $Stream.Position = $script:CtrlOffset
        $Stream.Write([BitConverter]::GetBytes([uint32]$Final), 0, 4); $Stream.Flush()
        return $true
    } finally { $Stream.Dispose() }
}

# ---- text files with an unknown encoding (Elden Ring's GraphicsConfig.xml is UTF-16) ----
function Read-TextKeepEncoding([string]$Path) {
    $Bytes = [IO.File]::ReadAllBytes($Path)
    if ($Bytes.Length -ge 2 -and $Bytes[0] -eq 0xFF -and $Bytes[1] -eq 0xFE) { $Enc = New-Object Text.UnicodeEncoding($false, $true); $Skip = 2 }
    elseif ($Bytes.Length -ge 2 -and $Bytes[0] -eq 0xFE -and $Bytes[1] -eq 0xFF) { $Enc = New-Object Text.UnicodeEncoding($true, $true); $Skip = 2 }
    elseif ($Bytes.Length -ge 3 -and $Bytes[0] -eq 0xEF -and $Bytes[1] -eq 0xBB -and $Bytes[2] -eq 0xBF) { $Enc = New-Object Text.UTF8Encoding($true); $Skip = 3 }
    elseif ($Bytes.Length -ge 4 -and $Bytes[1] -eq 0 -and $Bytes[3] -eq 0) { $Enc = New-Object Text.UnicodeEncoding($false, $false); $Skip = 0 }
    else { $Enc = New-Object Text.UTF8Encoding($false); $Skip = 0 }
    return [PSCustomObject]@{ Text = $Enc.GetString($Bytes, $Skip, $Bytes.Length - $Skip); Encoding = $Enc }
}
function Write-TextKeepEncoding([string]$Path, [string]$Text, [Text.Encoding]$Enc) {
    $Body = $Enc.GetBytes($Text)
    $Bom = $Enc.GetPreamble()
    $All = New-Object byte[] ($Bom.Length + $Body.Length)
    [Array]::Copy($Bom, 0, $All, 0, $Bom.Length)
    [Array]::Copy($Body, 0, $All, $Bom.Length, $Body.Length)
    [IO.File]::WriteAllBytes($Path, $All)
}
function Get-ScreenMode([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $T = (Read-TextKeepEncoding $Path).Text
    $M = [regex]::Match($T, '<ScreenMode>\s*([^<\s]*)\s*</ScreenMode>')
    if ($M.Success) { return $M.Groups[1].Value } else { return $null }
}
function Set-ScreenMode([string]$Path, [string]$Mode) {
    # Changes only the <ScreenMode> element. Returns the old value, or $null when nothing was changed.
    $F = Read-TextKeepEncoding $Path
    $M = [regex]::Match($F.Text, '<ScreenMode>\s*([^<\s]*)\s*</ScreenMode>')
    if (-not $M.Success -or $M.Groups[1].Value -eq $Mode) { return $null }
    $New = $F.Text.Substring(0, $M.Index) + "<ScreenMode>$Mode</ScreenMode>" + $F.Text.Substring($M.Index + $M.Length)
    Write-TextKeepEncoding $Path $New $F.Encoding
    return $M.Groups[1].Value
}

function Close-ProcessGracefully([System.Diagnostics.Process]$Process, [int]$TimeoutSeconds) {
    # WM_CLOSE to every top-level window (including hidden/owned ones), then wait. Returns $true when the process exited.
    try { [void]$Process.CloseMainWindow() } catch { }
    foreach ($W in (Get-ProcessWindows $Process)) { [void][UrWin]::PostMessage($W.Handle, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) }
    return $Process.WaitForExit($TimeoutSeconds * 1000)
}
