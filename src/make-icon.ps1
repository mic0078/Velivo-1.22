# Generuje app.ico (16-256 px) i icon.png. Uruchom: pwsh -File make-icon.ps1
Add-Type -AssemblyName System.Drawing
$out = $PSScriptRoot

function New-RoundRect($x, $y, $w, $h, $r) {
    $p = New-Object Drawing.Drawing2D.GraphicsPath; $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); $p
}
function Pt($x, $y) { New-Object Drawing.PointF $x, $y }

function Draw-Icon([int]$n) {
    $bmp = New-Object Drawing.Bitmap $n, $n
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $s = $n / 256.0
    $indigo = [Drawing.Color]::FromArgb(255, 124, 58, 237)   # fiolet
    $cyan = [Drawing.Color]::FromArgb(255, 6, 182, 212)      # turkus

    # tlo: zaokraglony kwadrat z gradientem po przekatnej
    $bg = New-RoundRect (8 * $s) (8 * $s) (240 * $s) (240 * $s) (56 * $s)
    $g.FillPath((New-Object Drawing.Drawing2D.LinearGradientBrush((Pt 0 0), (Pt $n $n), $indigo, $cyan)), $bg)
    # miekki polysk: od przezroczystej bieli u gory do zera w polowie, dalej bez zmian (bez powtarzania)
    $gl = New-Object Drawing.Drawing2D.LinearGradientBrush((Pt 0 0), (Pt 0 $n), [Drawing.Color]::FromArgb(70, 255, 255, 255), [Drawing.Color]::FromArgb(0, 255, 255, 255))
    $blend = New-Object Drawing.Drawing2D.Blend 3
    $blend.Factors = [single[]](0, 1, 1); $blend.Positions = [single[]](0, 0.55, 1)
    $gl.Blend = $blend
    $g.FillPath($gl, $bg)

    # Velivo: biale "V" z zaokraglonymi koncami + smugi predkosci z lewej
    $vw = [Math]::Max(2.2, 34 * $s)
    $shadowPen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(60, 30, 10, 80)), $vw
    $vPen = New-Object Drawing.Pen ([Drawing.Color]::White), $vw
    foreach ($p in $shadowPen, $vPen) { $p.StartCap = 'Round'; $p.EndCap = 'Round'; $p.LineJoin = 'Round' }
    $v = [Drawing.PointF[]]((Pt (92*$s) (72*$s)), (Pt (140*$s) (184*$s)), (Pt (188*$s) (72*$s)))
    $vs = [Drawing.PointF[]]($v | ForEach-Object { Pt $_.X ($_.Y + 6*$s) })
    $g.DrawLines($shadowPen, $vs)
    $g.DrawLines($vPen, $v)
    if ($n -ge 24) {
        # smugi: coraz krotsze i bardziej przezroczyste
        # y, poczatek x, koniec x (tuz przed lewym ramieniem V), przezroczystosc
        $lines = @(@(104, 34, 78, 210), @(130, 46, 90, 150), @(156, 60, 102, 100))
        foreach ($l in $lines) {
            $sp = New-Object Drawing.Pen ([Drawing.Color]::FromArgb($l[3], 255, 255, 255)), ([Math]::Max(1.5, 11 * $s))
            $sp.StartCap = 'Round'; $sp.EndCap = 'Round'
            $g.DrawLine($sp, $l[1] * $s, $l[0] * $s, $l[2] * $s, $l[0] * $s)
        }
    }
    $g.Dispose(); $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()
foreach ($z in $sizes) {
    $b = Draw-Icon $z
    $ms = New-Object IO.MemoryStream; $b.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $pngs += , $ms.ToArray()
    if ($z -eq 256) { $b.Save((Join-Path $out 'icon.png')) }
    $b.Dispose()
}
# plik .ico z wpisami PNG
$fs = [IO.File]::Create((Join-Path $out 'app.ico')); $w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$off = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $z = $sizes[$i] % 256
    $w.Write([byte]$z); $w.Write([byte]$z); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$off)
    $off += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
