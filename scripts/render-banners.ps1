$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$relayRoot = Split-Path $PSScriptRoot -Parent
$relayArt = [Drawing.Image]::FromFile((Join-Path $relayRoot 'src\Assets\cover.png'))
function New-RelayRoundedPath([single]$x,[single]$y,[single]$w,[single]$h,[single]$r) {
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x,$y,$d,$d,180,90); $p.AddArc(($x+$w-$d),$y,$d,$d,270,90)
    $p.AddArc(($x+$w-$d),($y+$h-$d),$d,$d,0,90); $p.AddArc($x,($y+$h-$d),$d,$d,90,90)
    $p.CloseFigure(); return $p
}
function Write-RelayBannerText($g,[string]$text,[single]$size,[single]$x,[single]$y,[string]$color,[bool]$bold=$false,[single]$spacing=0) {
    $style = if ($bold) { [Drawing.FontStyle]::Bold } else { [Drawing.FontStyle]::Regular }
    $font = New-Object Drawing.Font('Consolas',$size,$style,[Drawing.GraphicsUnit]::Pixel)
    $brush = New-Object Drawing.SolidBrush([Drawing.ColorTranslator]::FromHtml($color))
    $format = [Drawing.StringFormat]::GenericTypographic.Clone()
    $format.FormatFlags = $format.FormatFlags -bor [Drawing.StringFormatFlags]::MeasureTrailingSpaces
    try {
        if ($spacing -eq 0) { $g.DrawString($text,$font,$brush,$x,$y,$format) }
        else { foreach ($letter in $text.ToCharArray()) { $s=$letter.ToString(); $g.DrawString($s,$font,$brush,$x,$y,$format); $x += $g.MeasureString($s,$font,1000,$format).Width + $spacing } }
    } finally { $format.Dispose(); $brush.Dispose(); $font.Dispose() }
}
try {
    foreach ($theme in @('dark','light')) {
        $palette = if ($theme -eq 'dark') { @('#151020','#3c2b55','#af85ff','#f4eeff','#b4a1ce') } else { @('#f7f3ff','#d8c7ec','#7848be','#2b1b40','#75618b') }
        $bitmap = New-Object Drawing.Bitmap(2400,480,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [Drawing.Graphics]::FromImage($bitmap)
        $shape = New-RelayRoundedPath 1 1 1198 238 18
        $background = New-Object Drawing.SolidBrush([Drawing.ColorTranslator]::FromHtml($palette[0]))
        $accent = New-Object Drawing.SolidBrush([Drawing.ColorTranslator]::FromHtml($palette[2]))
        $border = New-Object Drawing.Pen([Drawing.ColorTranslator]::FromHtml($palette[1]),1)
        try {
            $g.ScaleTransform(2,2)
            $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
            $g.Clear([Drawing.Color]::Transparent)
            $g.FillPath($background,$shape)
            $g.SetClip($shape)
            $g.FillRectangle($accent,1,24,5,192)
            # Reuse the original application illustration unchanged; no crop or redraw.
            $g.DrawImage($relayArt,[Drawing.RectangleF]::new(790,-13,410,273.3333))
            Write-RelayBannerText $g 'FEATURED PROJECT / WINDOWS' 13 42 29 $palette[2] $false 3
            Write-RelayBannerText $g 'RELAY' 57 38 63 $palette[3] $true
            Write-RelayBannerText $g 'TWITCH LIVE / TELEGRAM ALERTS / YOUR PC' 18 42 137 $palette[4]
            Write-RelayBannerText $g 'C# · WPF · TELEGRAM BOT API / BY JUSTEN0T' 13 42 190 $palette[4] $false 1
            $g.ResetClip(); $g.DrawPath($border,$shape)
            $bitmap.Save((Join-Path $relayRoot ('assets\cover-' + $theme + '.png')),[Drawing.Imaging.ImageFormat]::Png)
        } finally { $border.Dispose(); $accent.Dispose(); $background.Dispose(); $shape.Dispose(); $g.Dispose(); $bitmap.Dispose() }
    }
} finally { $relayArt.Dispose() }
