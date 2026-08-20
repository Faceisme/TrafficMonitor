# Generates Assets/app.ico (multi-size, PNG-compressed entries).
Add-Type -AssemblyName System.Drawing

function New-IconBitmap([int]$S) {
    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    # ---- rounded tile ----
    $r = [float]($S * 0.235)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $inset = [float]($S * 0.02)
    $x = $inset; $y = $inset; $w = $S - 2*$inset; $h = $S - 2*$inset
    $d = 2*$r
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x+$w-$d, $y, $d, $d, 270, 90)
    $path.AddArc($x+$w-$d, $y+$h-$d, $d, $d, 0, 90)
    $path.AddArc($x, $y+$h-$d, $d, $d, 90, 90)
    $path.CloseFigure()

    $c1 = [System.Drawing.Color]::FromArgb(255, 42, 48, 64)
    $c2 = [System.Drawing.Color]::FromArgb(255, 16, 19, 28)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0,0)),
        (New-Object System.Drawing.PointF([float]$S,[float]$S)), $c1, $c2)
    $g.FillPath($brush, $path)

    # subtle top highlight
    $penHi = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(46,255,255,255), [float]([Math]::Max(1.0, $S/64.0)))
    $g.DrawPath($penHi, $path)

    # ---- arrows ----
    function Add-Arrow($gr, [float]$cx, [float]$cy, [float]$aw, [float]$ah, [bool]$up, $color) {
        $p = New-Object System.Drawing.Drawing2D.GraphicsPath
        $headH = $aw * 1.05
        $stemW = $aw * 0.40
        $pts = New-Object 'System.Drawing.PointF[]' 3
        if ($up) {
            $tip = $cy - $ah
            $pts[0] = New-Object System.Drawing.PointF([float]$cx, [float]$tip)
            $pts[1] = New-Object System.Drawing.PointF([float]($cx - $aw), [float]($tip + $headH))
            $pts[2] = New-Object System.Drawing.PointF([float]($cx + $aw), [float]($tip + $headH))
            $p.AddPolygon($pts)
            $rect = New-Object System.Drawing.RectangleF([float]($cx - $stemW/2), [float]($tip + $headH*0.80), [float]$stemW, [float](2*$ah - $headH*0.80))
            $p.AddRectangle($rect)
        } else {
            $tip = $cy + $ah
            $pts[0] = New-Object System.Drawing.PointF([float]$cx, [float]$tip)
            $pts[1] = New-Object System.Drawing.PointF([float]($cx - $aw), [float]($tip - $headH))
            $pts[2] = New-Object System.Drawing.PointF([float]($cx + $aw), [float]($tip - $headH))
            $p.AddPolygon($pts)
            $rect = New-Object System.Drawing.RectangleF([float]($cx - $stemW/2), [float]($cy - $ah), [float]$stemW, [float](2*$ah - $headH*0.80))
            $p.AddRectangle($rect)
        }
        $b = New-Object System.Drawing.SolidBrush($color)
        $gr.FillPath($b, $p)
        $b.Dispose(); $p.Dispose()
    }

    $aw = [float]($S * 0.125)
    $ah = [float]($S * 0.235)
    $orange = [System.Drawing.Color]::FromArgb(255, 255, 176, 72)
    $blue   = [System.Drawing.Color]::FromArgb(255, 76, 194, 255)
    Add-Arrow $g ([float]($S*0.335)) ([float]($S*0.50)) $aw $ah $true  $orange
    Add-Arrow $g ([float]($S*0.665)) ([float]($S*0.50)) $aw $ah $false $blue

    $g.Dispose(); $brush.Dispose(); $penHi.Dispose(); $path.Dispose()
    return $bmp
}

$sizes = @(16,20,24,32,40,48,64,128,256)
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,@($s, $ms.ToArray())
    $bmp.Dispose(); $ms.Dispose()
}

$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $s = [int]$p[0]; $data = [byte[]]$p[1]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($p in $pngs) { $bw.Write([byte[]]$p[1]) }
$bw.Flush()

$target = Join-Path $PSScriptRoot "..\src\NetSpeed\Assets\app.ico"
[System.IO.File]::WriteAllBytes([System.IO.Path]::GetFullPath($target), $out.ToArray())
$bw.Dispose(); $out.Dispose()
Write-Host ("icon written: " + [System.IO.Path]::GetFullPath($target))
