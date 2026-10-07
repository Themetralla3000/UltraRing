# Restores Elden Ring (if minimised) and gives the keyboard/mouse back to the ULTRAKILL input window.
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern void keybd_event(byte k, byte s, uint f, UIntPtr e);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  public static IntPtr Find(uint pid) { IntPtr found = IntPtr.Zero; EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) { found = h; return false; } return true; }, IntPtr.Zero); return found; }
}
'@
$er = Get-Process eldenring -ErrorAction SilentlyContinue | Select-Object -First 1
$uk = Get-Process ULTRAKILL -ErrorAction SilentlyContinue | Select-Object -First 1
if ($er) { $h = [W]::Find([uint32]$er.Id); "ER window $h iconic=$([W]::IsIconic($h))"; [W]::ShowWindow($h, 9) | Out-Null }
Start-Sleep -Milliseconds 800
if ($uk) { $h2 = [W]::Find([uint32]$uk.Id); [W]::keybd_event(0x12,0,0,[UIntPtr]::Zero); [W]::keybd_event(0x12,0,2,[UIntPtr]::Zero); "UK window $h2 focus=$([W]::SetForegroundWindow($h2))" }
