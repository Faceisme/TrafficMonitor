<#
For every visible top-level window belonging to a normal running app, maximizes it (WinAPI only,
no mouse) and checks two things against NetSpeed's fullscreen-detection geometry:
  - would the current (loose, >=/<=) check misclassify it as fullscreen?
  - does it have WS_CAPTION (i.e. is it an ordinary bordered window, not real exclusive fullscreen)?

Restores each window afterwards. Never touches the mouse.
#>

Add-Type -AssemblyName System.Drawing

$sig = @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class F {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowTextLengthW(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int m);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int m);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint f);
  [DllImport("user32.dll")] public static extern int GetWindowLongW(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint f);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool GetMonitorInfoW(IntPtr h, ref MONITORINFO mi);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
}
"@
Add-Type -TypeDefinition $sig
[void][F]::SetProcessDPIAware()

$WS_CAPTION = 0x00C00000
$GWL_STYLE = -16
$SW_MAXIMIZE = 3
$SW_RESTORE = 9
$DWMWA_EXTENDED_FRAME_BOUNDS = 9

$myPid = (Get-Process -Id $PID).Id
$seen = New-Object System.Collections.Generic.HashSet[string]
$results = New-Object System.Collections.Generic.List[object]

$cb = [F+EnumProc]{
    param($h, $l)
    if (-not [F]::IsWindowVisible($h)) { return $true }
    $len = [F]::GetWindowTextLengthW($h)
    if ($len -eq 0) { return $true }

    $wp = 0
    [void][F]::GetWindowThreadProcessId($h, [ref]$wp)
    if ($wp -eq $myPid) { return $true }

    $t = New-Object System.Text.StringBuilder ($len + 1)
    [void][F]::GetWindowTextW($h, $t, $len + 1)
    $title = $t.ToString()
    if ([string]::IsNullOrWhiteSpace($title)) { return $true }

    $c = New-Object System.Text.StringBuilder 256
    [void][F]::GetClassNameW($h, $c, 256)
    $cls = $c.ToString()
    if ($cls -in @("Shell_TrayWnd","Shell_SecondaryTrayWnd","Progman","WorkerW")) { return $true }

    try {
        $procName = (Get-Process -Id $wp -ErrorAction Stop).ProcessName
    } catch { $procName = "?" }

    $key = "$cls|$title"
    if ($seen.Contains($key)) { return $true }
    [void]$seen.Add($key)

    $script:results.Add([pscustomobject]@{ H = $h; Title = $title; Class = $cls; Proc = $procName })
    return $true
}
[void][F]::EnumWindows($cb, [IntPtr]::Zero)

Write-Host ("candidate windows: {0}" -f $results.Count)
foreach ($w in $results) { Write-Host ("  [{0}] '{1}' class={2}" -f $w.Proc, $w.Title, $w.Class) }
Write-Host ""

foreach ($w in $results) {
    $h = $w.H

    $before = New-Object F+RECT
    [void][F]::GetWindowRect($h, [ref]$before)

    [void][F]::ShowWindow($h, $SW_MAXIMIZE)
    Start-Sleep -Milliseconds 250

    $r = New-Object F+RECT
    [void][F]::GetWindowRect($h, [ref]$r)

    $mon = [F]::MonitorFromWindow($h, 2)
    $mi = New-Object F+MONITORINFO
    $mi.cbSize = [System.Runtime.InteropServices.Marshal]::SizeOf([type][F+MONITORINFO])
    [void][F]::GetMonitorInfoW($mon, [ref]$mi)

    $style = [F]::GetWindowLongW($h, $GWL_STYLE)
    $hasCaption = ($style -band $WS_CAPTION) -ne 0

    $matchLoose = ($r.L -le $mi.rcMonitor.L) -and ($r.T -le $mi.rcMonitor.T) -and ($r.R -ge $mi.rcMonitor.R) -and ($r.B -ge $mi.rcMonitor.B)

    $excludedClass = $w.Class -in @("Progman","WorkerW","Shell_TrayWnd","Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow","ApplicationManager_DesktopShellWindow",
        "MultitaskingViewFrame","XamlExplorerHostIslandWindow")

    # Mirrors WindowHelper.IsFullscreenAppActive(): class exclusion first, THEN geometry.
    $realVerdict = (-not $excludedClass) -and $matchLoose

    $color = if ($realVerdict) { "Red" } else { "Green" }
    Write-Host ("[{0}] '{1}'" -f $w.Proc, $w.Title)
    Write-Host ("  maximized rect = {0},{1} {2},{3}   monitor = {4},{5} {6},{7}" -f $r.L,$r.T,$r.R,$r.B,$mi.rcMonitor.L,$mi.rcMonitor.T,$mi.rcMonitor.R,$mi.rcMonitor.B)
    Write-Host ("  WS_CAPTION present = {0}   class-excluded = {1}" -f $hasCaption, $excludedClass)
    Write-Host ("  loose geometry alone = {0}   ACTUAL IsFullscreenAppActive() verdict = {1}" -f $matchLoose, $realVerdict) -ForegroundColor $color
    Write-Host ""

    # restore to not disturb the user's window layout
    [void][F]::ShowWindow($h, $SW_RESTORE)
    Start-Sleep -Milliseconds 120
}
