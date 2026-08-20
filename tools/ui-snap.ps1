param(
    [string]$ProcName = "NetSpeedUiTest",
    [string]$Prefix = "snap-"
)

# Screenshot only. This script never moves the cursor and never injects clicks.

Add-Type -AssemblyName System.Drawing

$sig = @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class S {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int m);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
}
"@
Add-Type -TypeDefinition $sig
[void][S]::SetProcessDPIAware()

$outDir = $env:PROBE_OUT
if (-not $outDir) { $outDir = "." }

function Shot([string]$name, [int]$x, [int]$y, [int]$w, [int]$h) {
    $sw = [S]::GetSystemMetrics(0); $sh = [S]::GetSystemMetrics(1)
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

$proc = Get-Process $ProcName -ErrorAction SilentlyContinue
if (-not $proc) { Write-Host "$ProcName not running"; exit 1 }
$target = $proc.Id

$script:acc = @()
$cb = [S+EnumProc]{
    param($h, $l)
    $wp = 0
    [void][S]::GetWindowThreadProcessId($h, [ref]$wp)
    if ($wp -eq $target -and [S]::IsWindowVisible($h)) {
        $r = New-Object S+RECT
        [void][S]::GetWindowRect($h, [ref]$r)
        $c = New-Object System.Text.StringBuilder 256
        [void][S]::GetClassNameW($h, $c, 256)
        if (($r.R - $r.L) -gt 4 -and ($r.B - $r.T) -gt 4) {
            $script:acc += [pscustomobject]@{ H = $h; Cls = $c.ToString(); L = $r.L; T = $r.T; W = ($r.R - $r.L); Ht = ($r.B - $r.T) }
        }
    }
    return $true
}
[void][S]::EnumWindows($cb, [IntPtr]::Zero)

$i = 0
foreach ($w in $script:acc) {
    Write-Host ("win{0}: [{1}] {2},{3} {4}x{5}" -f $i, $w.Cls, $w.L, $w.T, $w.W, $w.Ht)
    Shot ("{0}win{1}.png" -f $Prefix, $i) ($w.L - 12) ($w.T - 12) ($w.W + 24) ($w.Ht + 24)
    $i++
}
if ($i -eq 0) { Write-Host "no visible windows for $ProcName" }
