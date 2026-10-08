<#
.SYNOPSIS
  Restores Elden Ring (if minimised, and repositions it if it is stuck off-screen) and gives the keyboard/mouse back
  to ULTRAKILL's input window.
#>
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'BridgeCommon.ps1')
Add-Type -AssemblyName System.Windows.Forms
[void][UrWin]::SetProcessDPIAware()

$Er = Get-Process eldenring -ErrorAction SilentlyContinue | Select-Object -First 1
$Uk = Get-Process ULTRAKILL -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $Er -and -not $Uk) { Write-Output 'Neither Elden Ring nor ULTRAKILL is running.'; return }

# ULTRAKILL's window: prefer the visible Unity one, then any Unity one (even hidden/owned), then any visible window.
$UkWin = $null
if ($Uk) {
    $UkWins = @(Get-ProcessWindows $Uk)
    $UkWin = @($UkWins | Where-Object { $_.Class -eq 'UnityWndClass' -and $_.Visible } | Select-Object -First 1)[0]
    if (-not $UkWin) { $UkWin = @($UkWins | Where-Object { $_.Class -eq 'UnityWndClass' } | Select-Object -First 1)[0] }
    if (-not $UkWin) { $UkWin = @($UkWins | Where-Object { $_.Visible } | Select-Object -First 1)[0] }
}

if ($Er) {
    # Elden Ring's window: the largest visible top-level window of the process.
    $ErWin = @(Get-ProcessWindows $Er | Where-Object { $_.Visible } | Sort-Object { $_.Width * $_.Height } -Descending | Select-Object -First 1)[0]
    if (-not $ErWin) {
        Write-Warning 'Elden Ring has no visible window yet.'
    } else {
        $H = $ErWin.Handle
        Write-Output ("Elden Ring window: {0} {1}x{2} at {3},{4} minimised={5}" -f $H, $ErWin.Width, $ErWin.Height, $ErWin.Left, $ErWin.Top, $ErWin.Iconic)
        if ($ErWin.Iconic) { [void][UrWin]::ShowWindow($H, 9); Start-Sleep -Milliseconds 400 }
        $R = New-Object UrWin+RECT
        [void][UrWin]::GetWindowRect($H, [ref]$R)
        $W = $R.Right - $R.Left; $Ht = $R.Bottom - $R.Top
        if ($R.Left -le -30000 -or $R.Top -le -30000 -or $W -lt 320 -or $Ht -lt 200) {
            # Stuck at -32000,-32000 (tiny): put it on the monitor ULTRAKILL is on, else the primary one.
            $Screen = [Windows.Forms.Screen]::PrimaryScreen
            if ($UkWin -and $UkWin.Left -gt -30000) { $Screen = [Windows.Forms.Screen]::FromHandle($UkWin.Handle) }
            $B = $Screen.Bounds
            # SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW
            [void][UrWin]::SetWindowPos($H, [IntPtr]::Zero, $B.X, $B.Y, $B.Width, $B.Height, 0x0004 -bor 0x0010 -bor 0x0040)
            Write-Output ("Elden Ring window was off-screen/tiny; moved to {0},{1} {2}x{3}." -f $B.X, $B.Y, $B.Width, $B.Height)
        }
    }
}
Start-Sleep -Milliseconds 500

if ($Uk) {
    if (-not $UkWin) {
        Write-Warning 'No ULTRAKILL window found.'
    } else {
        # A synthetic Alt press lets SetForegroundWindow succeed from a background process.
        [UrWin]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); [UrWin]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
        $Ok = [UrWin]::SetForegroundWindow($UkWin.Handle)
        Write-Output ("ULTRAKILL window: {0} class={1} visible={2} focus={3}" -f $UkWin.Handle, $UkWin.Class, $UkWin.Visible, $Ok)
    }
} else {
    Write-Output 'ULTRAKILL is not running.'
}
