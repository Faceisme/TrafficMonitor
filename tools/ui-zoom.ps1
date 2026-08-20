param(
    [Parameter(Mandatory = $true)][string]$ProcName,
    [string]$Prefix = "zoom-",
    [int]$Scale = 4
)

# Screenshot only, magnified with nearest-neighbour so glyph rasterisation is visible.
# Never moves the cursor, never injects clicks.

Add-Type -AssemblyName System.Drawing

$sig = @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class Z {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
Add-Type -TypeDefinition $sig
[void][Z]::SetProcessDPIAware()

$outDir = $env:PROBE_OUT
if (-not $outDir) { $outDir = "." }

$proc = Get-Process $ProcName -ErrorAction SilentlyContinue
if (-not $proc) { Write-Host "$ProcName not running"; exit 1 }
$target = $proc.Id

$script:acc = @()
$cb = [Z+EnumProc]{
    param($h, $l)
    $wp = 0
    [void][Z]::GetWindowThreadProcessId($h, [ref]$wp)
    if ($wp -eq $target -and [Z]::IsWindowVisible($h)) {
        $r = New-Object Z+RECT
        [void][Z]::GetWindowRect($h, [ref]$r)
        if (($r.R - $r.L) -gt 4 -and ($r.B - $r.T) -gt 4) {
            $script:acc += [pscustomobject]@{ L = $r.L; T = $r.T; W = ($r.R - $r.L); Ht = ($r.B - $r.T) }
        }
    }
    return $true
}
[void][Z]::EnumWindows($cb, [IntPtr]::Zero)

# The widget is the smallest window the app owns.
$w = $script:acc | Sort-Object Ht | Select-Object -First 1
if (-not $w) { Write-Host "no window"; exit 1 }
Write-Host ("widget: {0},{1} {2}x{3}" -f $w.L, $w.T, $w.W, $w.Ht)

$grab = New-Object System.Drawing.Bitmap($w.W, $w.Ht)
$g = [System.Drawing.Graphics]::FromImage($grab)
$g.CopyFromScreen($w.L, $w.T, 0, 0, $grab.Size)
$g.Dispose()

$big = New-Object System.Drawing.Bitmap(($w.W * $Scale), ($w.Ht * $Scale))
$gb = [System.Drawing.Graphics]::FromImage($big)
$gb.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$gb.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
$gb.DrawImage($grab, 0, 0, $big.Width, $big.Height)
$gb.Dispose()

$path = Join-Path $outDir ($Prefix + "widget.png")
$big.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
$grab.Dispose(); $big.Dispose()
Write-Host "shot: $path (x$Scale)"
