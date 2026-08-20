<#
Diagnoses the taskbar flicker.

Samples, at high frequency, which window owns the pixel at the centre of the NetSpeed readout,
plus the readout's rect and visibility. Halfway through it toggles the tray overflow button through
UI Automation, so the cursor is never moved and no click is injected.

Output is a timeline of state changes: if the readout drops behind Shell_TrayWnd, or moves, or is
hidden, it shows up here with a timestamp.
#>
param(
    [string]$ProcName = "NetSpeed",
    [int]$Seconds = 7,
    [switch]$ListButtons
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$sig = @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class D {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct PT { public int X, Y; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int m);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(PT p);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint f);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
Add-Type -TypeDefinition $sig
[void][D]::SetProcessDPIAware()

function ClassOf([IntPtr]$h) {
    if ($h -eq [IntPtr]::Zero) { return "<null>" }
    $sb = New-Object System.Text.StringBuilder 256
    [void][D]::GetClassNameW($h, $sb, 256)
    return $sb.ToString()
}

# ---- locate the readout window (smallest visible window the app owns) ----
$proc = Get-Process $ProcName -ErrorAction SilentlyContinue
if (-not $proc) { Write-Host "$ProcName not running"; exit 1 }
$target = $proc.Id

$script:wins = @()
$cb = [D+EnumProc]{
    param($h, $l)
    $wp = 0
    [void][D]::GetWindowThreadProcessId($h, [ref]$wp)
    if ($wp -eq $target -and [D]::IsWindowVisible($h)) {
        $r = New-Object D+RECT
        [void][D]::GetWindowRect($h, [ref]$r)
        if (($r.R - $r.L) -gt 10 -and ($r.B - $r.T) -gt 10) {
            $script:wins += [pscustomobject]@{ H = $h; L = $r.L; T = $r.T; W = ($r.R - $r.L); Ht = ($r.B - $r.T) }
        }
    }
    return $true
}
[void][D]::EnumWindows($cb, [IntPtr]::Zero)

$widget = $script:wins | Sort-Object Ht | Select-Object -First 1
if (-not $widget) { Write-Host "readout window not found"; exit 1 }
Write-Host ("readout hwnd={0} rect={1},{2} {3}x{4}" -f $widget.H, $widget.L, $widget.T, $widget.W, $widget.Ht)

$probe = New-Object D+PT
$probe.X = [int]($widget.L + $widget.W / 2)
$probe.Y = [int]($widget.T + $widget.Ht / 2)
Write-Host ("probe point = {0},{1}" -f $probe.X, $probe.Y)

# ---- find the tray overflow button through UI Automation ----
$auto = [System.Windows.Automation.AutomationElement]
$trayCond = New-Object System.Windows.Automation.PropertyCondition($auto::ClassNameProperty, "Shell_TrayWnd")
$tray = $auto::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $trayCond)
if (-not $tray) { Write-Host "Shell_TrayWnd not found via UIA"; exit 1 }

$btnCond = New-Object System.Windows.Automation.PropertyCondition(
    $auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$buttons = $tray.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)

if ($ListButtons) {
    Write-Host "--- buttons under Shell_TrayWnd ---"
    foreach ($b in $buttons) { Write-Host ("  name='{0}' id='{1}'" -f $b.Current.Name, $b.Current.AutomationId) }
    exit 0
}

$chevron = $null
foreach ($b in $buttons) {
    $n = $b.Current.Name
    $id = $b.Current.AutomationId
    if ($n -match "隐藏|Hidden|溢出|Overflow" -or $id -match "Overflow|Chevron|SystemTrayIcon") { $chevron = $b; break }
}
if (-not $chevron) { Write-Host "overflow button not found; re-run with -ListButtons"; exit 1 }
Write-Host ("overflow button: name='{0}' id='{1}'" -f $chevron.Current.Name, $chevron.Current.AutomationId)

# ---- sample ----
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$deadline = $Seconds * 1000
$fireAt = 1500
$fireAgainAt = 4000
$fired = $false
$firedAgain = $false
$last = ""
$log = New-Object System.Collections.Generic.List[string]

while ($sw.ElapsedMilliseconds -lt $deadline) {
    $top = [D]::WindowFromPoint($probe)
    $topRoot = [D]::GetAncestor($top, 2)
    $vis = [D]::IsWindowVisible($widget.H)
    $r = New-Object D+RECT
    [void][D]::GetWindowRect($widget.H, [ref]$r)

    $owner = if ($topRoot -eq $widget.H) { "READOUT" } else { ClassOf $topRoot }
    $state = "{0} vis={1} rect={2},{3} {4}x{5}" -f $owner, $vis, $r.L, $r.T, ($r.R - $r.L), ($r.B - $r.T)

    if ($state -ne $last) {
        $log.Add(("{0,6} ms  {1}" -f $sw.ElapsedMilliseconds, $state))
        $last = $state
    }

    if (-not $fired -and $sw.ElapsedMilliseconds -ge $fireAt) {
        $fired = $true
        $log.Add(("{0,6} ms  >>> invoking overflow button" -f $sw.ElapsedMilliseconds))
        try {
            $p = $chevron.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            $p.Invoke()
        } catch { $log.Add("        invoke failed: $_") }
    }
    if (-not $firedAgain -and $sw.ElapsedMilliseconds -ge $fireAgainAt) {
        $firedAgain = $true
        $log.Add(("{0,6} ms  >>> invoking overflow button again (close)" -f $sw.ElapsedMilliseconds))
        try {
            $p = $chevron.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            $p.Invoke()
        } catch { $log.Add("        invoke failed: $_") }
    }

    Start-Sleep -Milliseconds 20
}

Write-Host "--- timeline (only state changes) ---"
$log | ForEach-Object { Write-Host $_ }
