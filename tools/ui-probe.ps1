param([int]$HoverSeconds = 2, [switch]$NoHover, [string]$ProcName = "NetSpeed", [string]$Prefix = "")

Add-Type -AssemblyName System.Drawing

$sig = @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class U {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct PT { public int X, Y; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out PT p);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int m);
}
"@
Add-Type -TypeDefinition $sig
[void][U]::SetProcessDPIAware()

$outDir = $env:PROBE_OUT
if (-not $outDir) { $outDir = "." }

function CropShot([string]$name, [int]$x, [int]$y, [int]$w, [int]$h) {
    $sw = [U]::GetSystemMetrics(0); $sh = [U]::GetSystemMetrics(1)
    if ($x -lt 0) { $x = 0 }
    if ($y -lt 0) { $y = 0 }
    if ($x + $w -gt $sw) { $w = $sw - $x }
    if ($y + $h -gt $sh) { $h = $sh - $y }
    if ($w -le 0 -or $h -le 0) { Write-Host "skip $name (empty region)"; return }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
    $g.Dispose()
    $path = Join-Path $outDir $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "shot: $path ($x,$y ${w}x${h})"
}

function AppWindows() {
    $proc = Get-Process $ProcName -ErrorAction SilentlyContinue
    if (-not $proc) { return @() }
    $target = $proc.Id
    $script:acc = @()
    $cb = [U+EnumProc]{
        param($h, $l)
        $wp = 0
        [void][U]::GetWindowThreadProcessId($h, [ref]$wp)
        if ($wp -eq $target -and [U]::IsWindowVisible($h)) {
            $r = New-Object U+RECT
            [void][U]::GetWindowRect($h, [ref]$r)
            $c = New-Object System.Text.StringBuilder 256
            [void][U]::GetClassNameW($h, $c, 256)
            if (($r.R - $r.L) -gt 10 -and ($r.B - $r.T) -gt 10 -and $c.ToString().StartsWith("HwndWrapper")) {
                $script:acc += [pscustomobject]@{ H = $h; L = $r.L; T = $r.T; W = ($r.R - $r.L); Ht = ($r.B - $r.T) }
            }
        }
        return $true
    }
    [void][U]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:acc
}

$tb = [U]::FindWindow("Shell_TrayWnd", [NullString]::Value)
$tr = New-Object U+RECT
[void][U]::GetWindowRect($tb, [ref]$tr)
Write-Host ("taskbar: {0},{1} {2}x{3}" -f $tr.L, $tr.T, ($tr.R-$tr.L), ($tr.B-$tr.T))

$wins = AppWindows
foreach ($w in $wins) { Write-Host ("win: {0},{1} {2}x{3}" -f $w.L, $w.T, $w.W, $w.Ht) }

$widget = $wins | Where-Object { $_.T -ge $tr.T - 4 } | Select-Object -First 1
if (-not $widget) { $widget = $wins | Select-Object -First 1 }
if (-not $widget) { Write-Host "no app window"; exit 1 }

CropShot ($Prefix + "01-widget.png") ($widget.L - 420) $tr.T 900 ($tr.B - $tr.T)

if ($NoHover) { exit 0 }

$orig = New-Object U+PT
[void][U]::GetCursorPos([ref]$orig)
[void][U]::SetCursorPos([int]($widget.L + $widget.W / 2), [int]($widget.T + $widget.Ht / 2))
Start-Sleep -Seconds $HoverSeconds

$after = AppWindows
foreach ($w in $after) { Write-Host ("after: {0},{1} {2}x{3}" -f $w.L, $w.T, $w.W, $w.Ht) }

$pop = $after | Where-Object { $_.H -ne $widget.H -and $_.Ht -gt $widget.Ht } | Sort-Object -Property Ht -Descending | Select-Object -First 1
if ($pop) {
    CropShot ($Prefix + "02-popup.png") ($pop.L - 8) ($pop.T - 8) ($pop.W + 16) ($pop.Ht + 16 + ($tr.B - $tr.T))
} else {
    Write-Host "no flyout window appeared"
    CropShot ($Prefix + "02-none.png") ($widget.L - 500) ($tr.T - 700) 1100 (700 + ($tr.B - $tr.T))
}

[void][U]::SetCursorPos($orig.X, $orig.Y)
