param(
    [string]$ProcName = "NetSpeedUiTest",
    [string]$Prefix = "menu-"
)

Add-Type -AssemblyName System.Drawing

$sig = @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class I {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct PT { public int X, Y; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int m);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out PT p);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr e);
}
"@
Add-Type -TypeDefinition $sig
[void][I]::SetProcessDPIAware()

$outDir = $env:PROBE_OUT
if (-not $outDir) { $outDir = "." }

function Shot([string]$name, [int]$x, [int]$y, [int]$w, [int]$h) {
    $sw = [I]::GetSystemMetrics(0); $sh = [I]::GetSystemMetrics(1)
    if ($x -lt 0) { $x = 0 }
    if ($y -lt 0) { $y = 0 }
    if ($x + $w -gt $sw) { $w = $sw - $x }
    if ($y + $h -gt $sh) { $h = $sh - $y }
    if ($w -le 0 -or $h -le 0) { Write-Host "skip $name"; return }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
    $g.Dispose()
    $p = Join-Path $outDir $name
    $bmp.Save($p, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "shot: $p"
}

function AppWindows() {
    $proc = Get-Process $ProcName -ErrorAction SilentlyContinue
    if (-not $proc) { return @() }
    $target = $proc.Id
    $script:acc = @()
    $cb = [I+EnumProc]{
        param($h, $l)
        $wp = 0
        [void][I]::GetWindowThreadProcessId($h, [ref]$wp)
        if ($wp -eq $target -and [I]::IsWindowVisible($h)) {
            $r = New-Object I+RECT
            [void][I]::GetWindowRect($h, [ref]$r)
            $c = New-Object System.Text.StringBuilder 256
            [void][I]::GetClassNameW($h, $c, 256)
            if (($r.R - $r.L) -gt 10 -and ($r.B - $r.T) -gt 10) {
                $script:acc += [pscustomobject]@{ H = $h; Cls = $c.ToString(); L = $r.L; T = $r.T; W = ($r.R - $r.L); Ht = ($r.B - $r.T) }
            }
        }
        return $true
    }
    [void][I]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:acc
}

$wins = AppWindows
$widget = $wins | Sort-Object Ht | Select-Object -First 1
if (-not $widget) { Write-Host "widget not found"; exit 1 }
Write-Host ("widget: {0},{1} {2}x{3}" -f $widget.L, $widget.T, $widget.W, $widget.Ht)

$orig = New-Object I+PT
[void][I]::GetCursorPos([ref]$orig)

$cx = [int]($widget.L + $widget.W / 2)
$cy = [int]($widget.T + $widget.Ht / 2)
[void][I]::SetCursorPos($cx, $cy)
Start-Sleep -Milliseconds 900

# right click on our own widget
[void][I]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero)   # RIGHTDOWN
Start-Sleep -Milliseconds 60
[void][I]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero)   # RIGHTUP
Start-Sleep -Milliseconds 900

$after = AppWindows
foreach ($w in $after) { Write-Host ("win: [{0}] {1},{2} {3}x{4}" -f $w.Cls, $w.L, $w.T, $w.W, $w.Ht) }

$menu = $after | Where-Object { $_.Cls -like "*Popup*" -or $_.Cls -like "*Menu*" } | Select-Object -First 1
if ($menu) {
    Shot ($Prefix + "menu.png") ($menu.L - 10) ($menu.T - 10) ($menu.W + 20) ($menu.Ht + 20)
} else {
    Write-Host "no menu window matched by class; capturing the area above the widget"
    Shot ($Prefix + "menu.png") ($cx - 300) ($widget.T - 620) 700 640
}

# ESC closes the menu; fall back to a second right-click on our own widget
[void][I]::keybd_event(0x1B, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 40
[void][I]::keybd_event(0x1B, 0, 2, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 500

[void][I]::SetCursorPos($orig.X, $orig.Y)
