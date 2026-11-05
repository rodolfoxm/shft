# Generates every brand asset from one geometry: SVGs, PNGs, the app icon and the installer pictures.
# Run with: powershell -NoProfile -STA -File scripts/make-branding.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'branding'
New-Item -ItemType Directory -Force $out, (Join-Path $root 'installer') | Out-Null

# Two parallel rounded bars on a 100x100 grid. The gap stays open at 16 px.
$bars = @(@(20, 74, 42, 26), @(58, 74, 80, 26))
$stroke = 20
$white = '#E6E7EA'; $black = '#0F1114'; $tile = '#171A1F'; $rim = '#343941'

function Color($hex) { [Windows.Media.ColorConverter]::ConvertFromString($hex) }
function Brush($hex) { $b = New-Object Windows.Media.SolidColorBrush (Color $hex); $b.Freeze(); $b }

function Bars-Svg($hex) {
    ($bars | ForEach-Object { '<line x1="{0}" y1="{1}" x2="{2}" y2="{3}" stroke="{4}" stroke-width="{5}" stroke-linecap="round"/>' -f $_[0], $_[1], $_[2], $_[3], $hex, $stroke }) -join ''
}
function Draw-Bars($dc, $hex, $x, $y, $size) {
    $s = $size / 100
    $pen = New-Object Windows.Media.Pen (Brush $hex), ($stroke * $s)
    $pen.StartLineCap = 'Round'; $pen.EndLineCap = 'Round'
    foreach ($b in $bars) {
        $dc.DrawLine($pen, (New-Object Windows.Point ($x + $b[0] * $s), ($y + $b[1] * $s)), (New-Object Windows.Point ($x + $b[2] * $s), ($y + $b[3] * $s)))
    }
}
function Word-Geometry($size) {
    $face = New-Object Windows.Media.Typeface (New-Object Windows.Media.FontFamily 'Segoe UI Variable Display'), 'Normal', 'SemiBold', 'Normal'
    $text = New-Object Windows.Media.FormattedText 'shft.', ([Globalization.CultureInfo]::InvariantCulture), 'LeftToRight', $face, $size, ([Windows.Media.Brushes]::White), 1.0
    $text.BuildGeometry((New-Object Windows.Point 0, 0))
}
function Png($draw, $w, $h, $file) {
    $v = New-Object Windows.Media.DrawingVisual
    $dc = $v.RenderOpen(); & $draw $dc; $dc.Close()
    $bmp = New-Object Windows.Media.Imaging.RenderTargetBitmap $w, $h, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($v)
    $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $m = New-Object IO.MemoryStream; $enc.Save($m)
    if ($file) { [IO.File]::WriteAllBytes($file, $m.ToArray()) }
    , $m.ToArray()
}
function Bmp($draw, $w, $h, $file) {
    $v = New-Object Windows.Media.DrawingVisual
    $dc = $v.RenderOpen(); & $draw $dc; $dc.Close()
    $bmp = New-Object Windows.Media.Imaging.RenderTargetBitmap $w, $h, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($v)
    $flat = New-Object Windows.Media.Imaging.FormatConvertedBitmap $bmp, ([Windows.Media.PixelFormats]::Bgr24), $null, 0
    $enc = New-Object Windows.Media.Imaging.BmpBitmapEncoder
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($flat))
    $f = [IO.File]::Create($file); $enc.Save($f); $f.Close()
}

# SVG: symbol and horizontal logo, white and black. The word is outlines, so no font is needed to show it.
$word = Word-Geometry 64
$wordBounds = $word.Bounds
$wordPath = $word.GetFlattenedPathGeometry(0.05, 'Relative').ToString([Globalization.CultureInfo]::InvariantCulture) -replace '^F[01]', ''
foreach ($v in @(@('white', $white), @('black', $black))) {
    $name, $hex = $v
    Set-Content (Join-Path $out "shft-symbol-$name.svg") -Encoding UTF8 -Value "<svg xmlns=`"http://www.w3.org/2000/svg`" viewBox=`"0 0 100 100`" role=`"img`" aria-label=`"shft.`">$(Bars-Svg $hex)</svg>"
    $dx = 112 - $wordBounds.Left; $dy = 50 - ($wordBounds.Top + $wordBounds.Bottom) / 2 + 2; $w = [Math]::Ceiling(112 + $wordBounds.Width + 8)
    Set-Content (Join-Path $out "shft-logo-$name.svg") -Encoding UTF8 -Value "<svg xmlns=`"http://www.w3.org/2000/svg`" viewBox=`"0 0 $w 100`" role=`"img`" aria-label=`"shft.`">$(Bars-Svg $hex)<path transform=`"translate($dx $dy)`" fill=`"$hex`" d=`"$wordPath`"/></svg>"
}
Set-Content (Join-Path $out 'shft-icon.svg') -Encoding UTF8 -Value "<svg xmlns=`"http://www.w3.org/2000/svg`" viewBox=`"0 0 100 100`" role=`"img`" aria-label=`"shft.`"><rect x=`"1`" y=`"1`" width=`"98`" height=`"98`" rx=`"22`" fill=`"$tile`" stroke=`"$rim`" stroke-width=`"2`"/><g transform=`"translate(17 17) scale(.66)`">$(Bars-Svg $white)</g></svg>"

# App icon: the symbol on a dark tile. At 16 and 24 px the symbol takes more of the tile, so it stays legible.
function Icon-Draw($size) {
    $inset = if ($size -le 24) { 0.1 } else { 0.17 }
    {
        param($dc)
        $edge = [Math]::Max(1, $size / 64)
        $pen = New-Object Windows.Media.Pen (Brush $rim), $edge
        $r = $size * 0.22
        $dc.DrawRoundedRectangle((Brush $tile), $pen, (New-Object Windows.Rect ($edge / 2), ($edge / 2), ($size - $edge), ($size - $edge)), $r, $r)
        Draw-Bars $dc $white ($size * $inset) ($size * $inset) ($size * (1 - 2 * $inset))
    }.GetNewClosure()
}
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @{}
foreach ($s in $sizes) { $pngs[$s] = Png (Icon-Draw $s) $s $s $null }
foreach ($s in 32, 48, 256) { [IO.File]::WriteAllBytes((Join-Path $out "shft-icon-$s.png"), $pngs[$s]) }
$o = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $o
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$off = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$pngs[$s].Length); $w.Write([uint32]$off); $off += $pngs[$s].Length
}
foreach ($s in $sizes) { $w.Write([byte[]]$pngs[$s]) }
[IO.File]::WriteAllBytes((Join-Path $root 'Assets\shft.ico'), $o.ToArray())

# Horizontal logo for the README and the installer's side picture.
$logo = {
    param($dc, $hex, $h)
    Draw-Bars $dc $hex 0 0 $h
    $g = Word-Geometry (0.64 * $h)
    $b = $g.Bounds
    $dc.PushTransform((New-Object Windows.Media.TranslateTransform ($h * 1.12 - $b.Left), ($h / 2 - ($b.Top + $b.Bottom) / 2 + $h * 0.02)))
    $dc.DrawGeometry((Brush $hex), $null, $g)
    $dc.Pop()
}
foreach ($v in @(@('white', $white), @('black', $black))) {
    $name, $hex = $v
    [void](Png { param($dc) & $logo $dc $hex 120 } 410 120 (Join-Path $out "shft-logo-$name.png"))
    [void](Png { param($dc) Draw-Bars $dc $hex 0 0 256 } 256 256 (Join-Path $out "shft-symbol-$name.png"))
}
Bmp {
    param($dc)
    $dc.DrawRectangle((Brush $black), $null, (New-Object Windows.Rect 0, 0, 240, 459))
    Draw-Bars $dc $white 60 150 120
} 240 459 (Join-Path $root 'installer\wizard.bmp')
Bmp {
    param($dc)
    $dc.DrawRectangle((Brush $black), $null, (New-Object Windows.Rect 0, 0, 58, 58))
    Draw-Bars $dc $white 7 7 44
} 58 58 (Join-Path $root 'installer\wizard-small.bmp')
Get-ChildItem $out | ForEach-Object Name
