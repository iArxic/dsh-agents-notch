<#
.SYNOPSIS
  Six takes on the terminal-flavoured banner: a BOLD notch, and five different treatments for the three
  states underneath.

.DESCRIPTION
  1280x640 each, drawn with GDI+ (Segoe UI + Cascadia Mono). The notch is the product's silhouette, so
  it is drawn big and with real weight here; the three states below it are the part that has to explain
  the thing at card size, and dots alone were too quiet.

  Shared idioms learned the hard way, kept in one place rather than re-learned per variant:
    * `Linear-Brush` calls the constructor explicitly — PowerShell mis-binds the overloads;
    * text is measured with `Text-Width`, not `Measure`, which is an alias for Measure-Object;
    * the hairline under the composition is a filled rectangle, not a Pen (a null Pen throws
      "Value cannot be null");
    * every count passed to FillRectangle is a [single] or a RectangleF, or GDI+ says "Parameter is
      not valid".

.EXAMPLE
  pwsh -File tools/design-banner-c.ps1
#>
[CmdletBinding()]
param([string] $OutDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'docs/design'))

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$W = 1280; $H = 640
# the banner prints the version; read it instead of typing it, or a bump leaves a stale picture
$pkgVersion = (Get-Content (Join-Path (Split-Path -Parent $PSScriptRoot) 'package.json') -Raw | ConvertFrom-Json).version

function New-C($hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }
function Solid($c) { [System.Drawing.SolidBrush]::new($c) }
function Linear-Brush($rect, $from, $to, $mode) { [System.Drawing.Drawing2D.LinearGradientBrush]::new($rect, $from, $to, $mode) }

function Font([single] $size, [string] $style = 'Regular') {
	if ($style -eq 'Semibold') {
		try { return [System.Drawing.Font]::new('Segoe UI Semibold', $size, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel) }
		catch { return [System.Drawing.Font]::new('Segoe UI', $size, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel) }
	}
	$fs = if ($style -eq 'Bold') { [System.Drawing.FontStyle]::Bold } else { [System.Drawing.FontStyle]::Regular }
	return [System.Drawing.Font]::new('Segoe UI', $size, $fs, [System.Drawing.GraphicsUnit]::Pixel)
}

function Mono([single] $size) {
	foreach ($family in 'Cascadia Mono', 'Consolas') {
		try { return [System.Drawing.Font]::new($family, $size, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel) } catch { }
	}
	return [System.Drawing.Font]::new('Consolas', $size, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
}

function Text-Width($g, [string] $s, $font) { $g.MeasureString($s, $font) }

function New-Bitmap([int] $w, [int] $h) {
	$bmp = [System.Drawing.Bitmap]::new($w, $h)
	$g = [System.Drawing.Graphics]::FromImage($bmp)
	$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
	$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
	$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
	return @{ bmp = $bmp; g = $g }
}

function Round-Rect([System.Drawing.RectangleF] $r, [single] $radius) {
	$p = [System.Drawing.Drawing2D.GraphicsPath]::new()
	$d = $radius * 2
	$p.AddArc($r.X, $r.Y, $d, $d, 180, 90)
	$p.AddArc($r.Right - $d, $r.Y, $d, $d, 270, 90)
	$p.AddArc($r.Right - $d, $r.Bottom - $d, $d, $d, 0, 90)
	$p.AddArc($r.X, $r.Bottom - $d, $d, $d, 90, 90)
	$p.CloseFigure()
	return $p
}

function Glow($g, [single] $cx, [single] $cy, [single] $radius, $color, [int] $alpha = 80) {
	$rect = [System.Drawing.RectangleF]::new($cx - $radius, $cy - $radius, $radius * 2, $radius * 2)
	$path = [System.Drawing.Drawing2D.GraphicsPath]::new()
	$path.AddEllipse($rect)
	$brush = [System.Drawing.Drawing2D.PathGradientBrush]::new($path)
	$brush.CenterColor = [System.Drawing.Color]::FromArgb($alpha, $color.R, $color.G, $color.B)
	$brush.SurroundColors = [System.Drawing.Color[]]@([System.Drawing.Color]::FromArgb(0, $color.R, $color.G, $color.B))
	$g.FillPath($brush, $path)
	$brush.Dispose(); $path.Dispose()
}

function Scroll-Background($g, [int] $w, [int] $h) {
	$rect = [System.Drawing.RectangleF]::new(0, 0, $w, $h)
	$lg = Linear-Brush $rect (New-C '#070A0F') (New-C '#0D1219') ([System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
	$g.FillRectangle($lg, $rect); $lg.Dispose()
	$grid = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(12, 255, 255, 255), [single]1)
	for ($x = 0; $x -lt $w; $x += 64) { $g.DrawLine($grid, [single]$x, [single]0, [single]$x, [single]$h) }
	for ($y = 0; $y -lt $h; $y += 64) { $g.DrawLine($grid, [single]0, [single]$y, [single]$w, [single]$y) }
	$grid.Dispose()
}

<#
  The bot. Rim = identity, badge + ring = state; the eyes carry the state too, because at banner size a
  badge is a dot and a face is a face.
#>
function Bot($g, [single] $cx, [single] $cy, [single] $r, $rim, [string] $state = 'working', [switch] $Bold) {
	$face = [System.Drawing.RectangleF]::new($cx - $r, $cy - $r, $r * 2, $r * 2)
	$fill = Linear-Brush $face (New-C '#F6FAFF') (New-C '#C9D6EA') ([System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
	$g.FillEllipse($fill, $face); $fill.Dispose()
	$w = [Math]::Max(2.0, $r * 0.12); if ($Bold) { $w = [Math]::Max(3.0, $r * 0.16) }
	$rimPen = [System.Drawing.Pen]::new($rim, [single]$w)
	$g.DrawEllipse($rimPen, $face); $rimPen.Dispose()

	$eye = Solid (New-C '#222A38')
	$dy = -$r * 0.04
	if ($state -eq 'done') {
		$pen = [System.Drawing.Pen]::new((New-C '#222A38'), [single]([Math]::Max(2.4, $r * 0.13)))
		$g.DrawArc($pen, ($cx - $r * 0.50), ($cy + $dy - $r * 0.10), ($r * 0.44), ($r * 0.40), 200, 140)
		$g.DrawArc($pen, ($cx + $r * 0.06), ($cy + $dy - $r * 0.10), ($r * 0.44), ($r * 0.40), 200, 140)
		$pen.Dispose()
	} elseif ($state -eq 'attention') {
		$g.FillEllipse($eye, ($cx - $r * 0.36), ($cy + $dy - $r * 0.30), ($r * 0.28), ($r * 0.60))
		$g.FillEllipse($eye, ($cx + $r * 0.08), ($cy + $dy - $r * 0.30), ($r * 0.28), ($r * 0.60))
	} else {
		$g.FillEllipse($eye, ($cx - $r * 0.34), ($cy + $dy - $r * 0.23), ($r * 0.18), ($r * 0.46))
		$g.FillEllipse($eye, ($cx + $r * 0.16), ($cy + $dy - $r * 0.23), ($r * 0.18), ($r * 0.46))
	}
	$eye.Dispose()

	if ($state -ne 'working') {
		$badge = if ($state -eq 'done') { New-C '#6FD08C' } else { New-C '#F2B34C' }
		$br = $r * 0.36
		$b = Solid $badge
		$g.FillEllipse($b, ($cx + $r * 0.52 - $br), ($cy + $r * 0.52 - $br), ($br * 2), ($br * 2))
		$b.Dispose()
		$ringPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(190, $badge.R, $badge.G, $badge.B), [single]([Math]::Max(2.0, $r * 0.09)))
		$g.DrawEllipse($ringPen, ($cx - $r - 5), ($cy - $r - 5), (($r + 5) * 2), (($r + 5) * 2))
		$ringPen.Dispose()
	}
}

<# The notch. `Bold` is the point of this file: heavier body, heavier rim, a real drop shadow. #>
function Notch($g, [single] $cx, [single] $top, [single] $scale, [string[]] $states, [string] $label, [switch] $Bold) {
	$w = 300 * $scale; $h = 56 * $scale
	$rect = [System.Drawing.RectangleF]::new($cx - $w / 2, $top, $w, $h)

	if ($Bold) {
		# the shadow: three offset, low-alpha rounded rects rather than a blur
		for ($i = 3; $i -ge 1; $i--) {
			$sh = [System.Drawing.RectangleF]::new($rect.X - $i, $rect.Y + 6 + $i, $rect.Width + $i * 2, $rect.Height + $i)
			$sb = Solid ([System.Drawing.Color]::FromArgb(22, 0, 0, 0))
			$p = Round-Rect $sh ($h / 2)
			$g.FillPath($sb, $p); $p.Dispose(); $sb.Dispose()
		}
	}
	$body = Linear-Brush $rect (New-C '#171C25') (New-C '#0C1017') ([System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
	$bodyPath = Round-Rect $rect ($h / 2)
	$g.FillPath($body, $bodyPath); $body.Dispose(); $bodyPath.Dispose()

	$rimW = if ($Bold) { 2.4 } else { 1.2 }
	$rimPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb($(if ($Bold) { 120 } else { 70 }), 255, 255, 255), [single]$rimW)
	$rimPath = Round-Rect $rect ($h / 2)
	$g.DrawPath($rimPen, $rimPath); $rimPath.Dispose(); $rimPen.Dispose()

	# a specular line along the top edge, which is what makes it read as glass
	if ($Bold) {
		$hl = [System.Drawing.RectangleF]::new($rect.X + $h * 0.35, $rect.Y + 2.5, $rect.Width - $h * 0.7, 2)
		$hb = Linear-Brush ([System.Drawing.RectangleF]::new($hl.X, $hl.Y, $hl.Width, $hl.Height)) ([System.Drawing.Color]::FromArgb(0, 255, 255, 255)) ([System.Drawing.Color]::FromArgb(0, 255, 255, 255)) ([System.Drawing.Drawing2D.LinearGradientMode]::Horizontal)
		$g.FillRectangle($hb, $hl); $hb.Dispose()
	}

	$rims = @((New-C '#59D3C6'), (New-C '#E88FC4'), (New-C '#F2B34C'))
	$radius = $h * 0.29
	$bx = $rect.X + $h * 0.46
	for ($i = 0; $i -lt $states.Count; $i++) {
		Bot $g ($bx + $i * $h * 0.40) ($rect.Y + $h / 2) $radius $rims[$i % 3] $states[$i] -Bold:$Bold
	}

	$lf = Font ([single](16 * $scale)) 'Semibold'
	$lb = Solid (New-C '#EEF3FA')
	$g.DrawString($label, $lf, $lb, ($rect.X + $h * 0.66 + $states.Count * $h * 0.40), ($rect.Y + $h / 2 - (11 * $scale)))
	$lb.Dispose(); $lf.Dispose()

	$cf = Mono ([single](14 * $scale))
	$cb = Solid (New-C '#8EA0B6')
	$g.DrawString([string][char]0x25BE, $cf, $cb, ($rect.Right - $h * 0.46), ($rect.Y + $h / 2 - (9 * $scale)))
	$cb.Dispose(); $cf.Dispose()
}

# ── the five treatments for "the three things underneath" ────────────────────────────────────────
$STATES = @(
	@{ key = 'working'; dot = '#59D3C6'; text = 'working'; blurb = 'breathing eyes' },
	@{ key = 'attention'; dot = '#F2B34C'; text = 'needs you'; blurb = 'wide eyes, amber ring' },
	@{ key = 'done'; dot = '#6FD08C'; text = 'done'; blurb = 'a settled arc' }
)

function Legend-Chips($g, [single] $cx, [single] $y) {
	# three pills, evenly spaced under the notch
	$chipH = 34; $gap = 12
	$f = Mono 14
	$widths = @()
	foreach ($s in $STATES) { $widths += [single](Text-Width $g $s.text $f).Width + 46 }
	$total = ($widths | Measure-Object -Sum).Sum + $gap * 2
	$x = $cx - $total / 2
	$i = 0
	foreach ($s in $STATES) {
		$r = [System.Drawing.RectangleF]::new($x, $y, $widths[$i], $chipH)
		$p = Round-Rect $r ($chipH / 2)
		$b = Solid ([System.Drawing.Color]::FromArgb(30, 255, 255, 255))
		$g.FillPath($b, $p); $b.Dispose()
		$pen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(38, 255, 255, 255), [single]1)
		$g.DrawPath($pen, $p); $pen.Dispose(); $p.Dispose()
		$dot = Solid (New-C $s.dot)
		$g.FillEllipse($dot, ($x + 14), ($y + $chipH / 2 - 5), 10, 10); $dot.Dispose()
		$tb = Solid (New-C '#AFBFD2')
		$g.DrawString($s.text, $f, $tb, ($x + 30), ($y + 8)); $tb.Dispose()
		$x += $widths[$i] + $gap
		$i++
	}
	$f.Dispose()
}

function Legend-Bots($g, [single] $cx, [single] $y) {
	# three small bots with their label underneath: the legend IS the product's own faces
	$rims = @((New-C '#59D3C6'), (New-C '#E88FC4'), (New-C '#F2B34C'))
	$spacing = 150
	$x = $cx - $spacing
	$i = 0
	foreach ($s in $STATES) {
		Bot $g $x $y 22 $rims[$i % 3] $s.key -Bold
		$f = Mono 13; $b = Solid (New-C '#93A3B8')
		$t = $s.text
		$g.DrawString($t, $f, $b, ($x - (Text-Width $g $t $f).Width / 2), ($y + 32)); $b.Dispose(); $f.Dispose()
		$x += $spacing
		$i++
	}
}

function Legend-List($g, [single] $cx, [single] $y) {
	# three rows, a word and a line: quieter than chips, more informative
	$left = $cx - 210
	$rowY = $y
	foreach ($s in $STATES) {
		$dot = Solid (New-C $s.dot)
		$g.FillEllipse($dot, $left, ($rowY + 6), 9, 9); $dot.Dispose()
		$f = Mono 15; $b = Solid (New-C '#B7C6D9')
		$g.DrawString($s.text.PadRight(12), $f, $b, ($left + 22), ($rowY)); $b.Dispose(); $f.Dispose()
		$f2 = Mono 13; $b2 = Solid (New-C '#5E7086')
		$g.DrawString($s.blurb, $f2, $b2, ($left + 190), ($rowY + 2)); $b2.Dispose(); $f2.Dispose()
		$rowY += 34
	}
}

function Legend-Words($g, [single] $cx, [single] $y) {
	# the words themselves, big and spaced: no chrome at all
	$f = Mono 18
	$items = @($STATES | ForEach-Object { $_.text.ToUpper() })
	$sep = '   ·   '
	$widths = @()
	foreach ($s in $items) { $widths += (Text-Width $g $s $f).Width }
	$sepW = (Text-Width $g $sep $f).Width
	$total = ($widths | Measure-Object -Sum).Sum + $sepW * ($items.Count - 1)
	$x = $cx - $total / 2
	for ($i = 0; $i -lt $items.Count; $i++) {
		$b = Solid (New-C $STATES[$i].dot)
		$g.DrawString($items[$i], $f, $b, $x, $y); $b.Dispose()
		$x += $widths[$i]
		if ($i -lt $items.Count - 1) {
			$sb = Solid (New-C '#3E4C5E')
			$g.DrawString($sep, $f, $sb, $x, $y); $sb.Dispose()
			$x += $sepW
		}
	}
	$f.Dispose()
}

function Legend-Bar($g, [single] $cx, [single] $y) {
	# one pill divided into three cells — the board's own tab, at rest
	$h = 40; $cellW = 150
	$w = $cellW * 3
	$rect = [System.Drawing.RectangleF]::new($cx - $w / 2, $y, $w, $h)
	$p = Round-Rect $rect ($h / 2)
	$b = Solid ([System.Drawing.Color]::FromArgb(46, 12, 16, 23))
	$g.FillPath($b, $p); $b.Dispose()
	$pen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(40, 255, 255, 255), [single]1)
	$g.DrawPath($pen, $p); $pen.Dispose(); $p.Dispose()
	$f = Mono 13
	for ($i = 0; $i -lt 3; $i++) {
		$s = $STATES[$i]
		if ($i -gt 0) {
			$divider = Solid ([System.Drawing.Color]::FromArgb(30, 255, 255, 255))
			$g.FillRectangle($divider, [single]($rect.X + $cellW * $i), [single]($y + 8), [single]1, [single]($h - 16))
			$divider.Dispose()
		}
		$dot = Solid (New-C $s.dot)
		$g.FillEllipse($dot, ($rect.X + $cellW * $i + 22), ($y + $h / 2 - 4), 9, 9); $dot.Dispose()
		$tb = Solid (New-C '#A9B9CC')
		$g.DrawString($s.text, $f, $tb, ($rect.X + $cellW * $i + 38), ($y + $h / 2 - 8)); $tb.Dispose()
	}
	$f.Dispose()
}

<#
  One variant: shared left column (install line, wordmark, purpose), then the notch and the chosen
  legend on the right. `Legend` and `NotchScale` are the two variables across the six.
#>
function Variant([string] $path, [string] $legend, [single] $scale, [single] $notchTop) {
	$o = New-Bitmap $W $H; $g = $o.g
	Scroll-Background $g $W $H
	Glow $g 900 250 430 (New-C '#2E7D74') 96

	$f0 = Mono 15; $b0 = Solid (New-C '#6E7F94')
	$g.DrawString('PS C:\> dsh plugin --profile desktop add dsh-agents-notch', $f0, $b0, 72, 88); $b0.Dispose(); $f0.Dispose()
	$f1 = Font 62 'Semibold'; $b1 = Solid (New-C '#F2F6FC')
	$g.DrawString('Agents', $f1, $b1, 68, 128)
	$g.DrawString('Notch', $f1, $b1, 68, 200); $b1.Dispose(); $f1.Dispose()
	$f2 = Font 20; $b2 = Solid (New-C '#9FB0C6')
	$g.DrawString('Which session is working.', $f2, $b2, 72, 306)
	$g.DrawString('Which one needs you.', $f2, $b2, 72, 336)
	$g.DrawString('Which are done.', $f2, $b2, 72, 366); $b2.Dispose(); $f2.Dispose()
	$f3 = Mono 15; $b3 = Solid (New-C '#5F7085')
	$g.DrawString('windows x64  ·  self-contained  ·  no .NET runtime', $f3, $b3, 72, 470); $b3.Dispose(); $f3.Dispose()

	Notch $g 900 $notchTop $scale @('working', 'attention') '1 needs you' -Bold

	$legendY = $notchTop + 56 * $scale + 34
	switch ($legend) {
		'chips' { Legend-Chips $g 900 $legendY }
		'bots' { Legend-Bots $g 900 ($legendY + 8) }
		'list' { Legend-List $g 900 $legendY }
		'words' { Legend-Words $g 900 ($legendY + 4) }
		'bar' { Legend-Bar $g 900 $legendY }
	}

	# the floor
	$hair = Solid ([System.Drawing.Color]::FromArgb(30, 255, 255, 255))
	$g.FillRectangle($hair, [single]72, [single]566, [single]1136, [single]1); $hair.Dispose()
	$ff = Mono 14; $fb = Solid (New-C '#54657A')
	$g.DrawString(("dsh-agents-notch  ·  v{0}" -f $pkgVersion), $ff, $fb, 72, 584)
	$right = 'mit  ·  github.com/iArxic/dsh-agents-notch'
	$g.DrawString($right, $ff, $fb, (1208 - (Text-Width $g $right $ff).Width), 584)
	$fb.Dispose(); $ff.Dispose()

	$g.Dispose(); $o.bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $o.bmp.Dispose()
}

$variants = [ordered]@{
	'C1' = @{ legend = 'chips'; scale = 1.65; top = 132 }   # bold notch, three pills
	'C2' = @{ legend = 'bots'; scale = 1.65; top = 128 }    # bold notch, three little faces
	'C3' = @{ legend = 'list'; scale = 1.85; top = 118 }    # biggest notch, a three-line key
	'C4' = @{ legend = 'words'; scale = 1.65; top = 136 }   # bold notch, words only
	'C5' = @{ legend = 'bar'; scale = 1.5; top = 150 }      # bold notch, a three-cell tab
	'C6' = @{ legend = 'chips'; scale = 2.0; top = 104 }    # largest notch, pills tucked under
}

foreach ($k in $variants.Keys) {
	$v = $variants[$k]
	$file = Join-Path $OutDir "$k.png"
	Variant $file $v.legend ([single]$v.scale) ([single]$v.top)
	"drew $file"
}

# contact sheet, 2 x 3
$sheet = [System.Drawing.Bitmap]::new(($W / 2) * 3 + 24, ($H / 2) * 2 + 18)
$sg = [System.Drawing.Graphics]::FromImage($sheet)
$sg.Clear([System.Drawing.Color]::FromArgb(255, 3, 4, 6))
$sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$i = 0
foreach ($k in $variants.Keys) {
	$src = [System.Drawing.Image]::FromFile((Join-Path $OutDir "$k.png"))
	$x = 8 + ($i % 3) * ($W / 2 + 4); $y = 8 + [Math]::Floor($i / 3) * ($H / 2 + 4)
	$sg.DrawImage($src, [int]$x, [int]$y, [int]($W / 2), [int]($H / 2))
	$label = Font 20 'Bold'
	$lb = Solid ([System.Drawing.Color]::FromArgb(255, 255, 255, 255))
	$sg.DrawString($k, $label, $lb, [int]($x + 8), [int]($y + 6))
	$lb.Dispose(); $label.Dispose(); $src.Dispose()
	$i++
}
$sg.Dispose()
$boardPath = Join-Path $OutDir 'board-c.png'
$sheet.Save($boardPath, [System.Drawing.Imaging.ImageFormat]::Png)
$sheet.Dispose()
"contact sheet: $boardPath"

# ════════════════════════════════════════════════════════════════════════════════════════════════
# The LEFT block: the wordmark and the three lines under it. Same right side in every variant (the
# bold notch + pills from C1), because the question here is only how the title and the three things
# are set. Segoe UI Black is used where the wordmark must carry real weight; it falls back to Bold.
# ════════════════════════════════════════════════════════════════════════════════════════════════

function Font-Black([single] $size) {
	foreach ($family in 'Segoe UI Black', 'Segoe UI') {
		try { return [System.Drawing.Font]::new($family, $size, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel) } catch { }
	}
	return [System.Drawing.Font]::new('Segoe UI', $size, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
}

function Left-Eyebrow($g, [string] $text, [single] $y) {
	$f = Mono 14; $b = Solid (New-C '#63758C')
	$g.DrawString($text, $f, $b, 72, $y); $b.Dispose(); $f.Dispose()
}

function Left-Wordmark($g, [single] $size, [single] $x, [single] $y, [string] $accentWord = 'Notch', [switch] $Stacked, [switch] $Upper) {
	$one = if ($Upper) { 'AGENTS' } else { 'Agents' }
	$two = if ($Upper) { 'NOTCH' } else { 'Notch' }
	$f = Font-Black $size
	$w = Solid (New-C '#F5F8FD')
	$a = Solid (New-C '#59D3C6')
	$lineH = $size * 1.06
	if ($Stacked) {
		$g.DrawString($one, $f, $w, $x, $y)
		$g.DrawString($two, $f, $a, $x, ($y + $lineH))
	} else {
		$g.DrawString($one, $f, $w, $x, $y)
		$g.DrawString($two, $f, $a, ($x + (Text-Width $g ($one + ' ') $f).Width), $y)
	}
	$w.Dispose(); $a.Dispose(); $f.Dispose()
	return $lineH
}

# three lines, each a bold state and a muted gloss
function Left-StateLines($g, [single] $x, [single] $y, [single] $leadSize = 20) {
	$rows = @(
		@{ lead = 'Working'; rest = '  breathing eyes, a moving chevron' },
		@{ lead = 'Needs you'; rest = '  wide eyes and an amber ring' },
		@{ lead = 'Done'; rest = '  a settled arc, and the count' }
	)
	$fl = Font $leadSize 'Semibold'
	$fr = Font $leadSize
	foreach ($r in $rows) {
		$bw = Solid (New-C '#EAF1FA')
		$g.DrawString($r.lead, $fl, $bw, $x, $y); $bw.Dispose()
		$bm = Solid (New-C '#8FA0B5')
		$g.DrawString($r.rest, $fr, $bm, ($x + (Text-Width $g $r.lead $fl).Width), $y); $bm.Dispose()
		$y += $leadSize * 1.55
	}
	$fl.Dispose(); $fr.Dispose()
}

function Left-Numbered($g, [single] $x, [single] $y) {
	$rows = @(
		@{ n = '01'; t = 'every session is one bot, with its own rim' },
		@{ n = '02'; t = 'the rim is identity, the badge is state' },
		@{ n = '03'; t = 'nothing is invented when a field is unknown' }
	)
	$fn = Mono 14; $ft = Mono 14
	foreach ($r in $rows) {
		$bn = Solid (New-C '#4F6076')
		$g.DrawString($r.n, $fn, $bn, $x, $y); $bn.Dispose()
		$bt = Solid (New-C '#9FB0C6')
		$g.DrawString($r.t, $ft, $bt, ($x + 46), $y); $bt.Dispose()
		$y += 30
	}
	$fn.Dispose(); $ft.Dispose()
}

# stacked state pills, each with its coloured dot — the legend moved into the left column
function Left-StatePills($g, [single] $x, [single] $y) {
	$h = 36; $w = 210
	$f = Mono 15
	$i = 0
	foreach ($s in $STATES) {
		$r = [System.Drawing.RectangleF]::new($x, $y, $w, $h)
		$p = Round-Rect $r ($h / 2)
		$b = Solid ([System.Drawing.Color]::FromArgb(30, 255, 255, 255))
		$g.FillPath($b, $p); $b.Dispose()
		$pen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(40, 255, 255, 255), [single]1)
		$g.DrawPath($pen, $p); $pen.Dispose(); $p.Dispose()
		$dot = Solid (New-C $STATES[$i].dot)
		$g.FillEllipse($dot, ($x + 16), ($y + $h / 2 - 5), 10, 10); $dot.Dispose()
		$tb = Solid (New-C '#C2D0E2')
		$g.DrawString($STATES[$i].text, $f, $tb, ($x + 34), ($y + 9)); $tb.Dispose()
		$y += $h + 10
		$i++
	}
	$f.Dispose()
}

function Right-Side($g) {
	Glow $g 900 250 430 (New-C '#2E7D74') 96
	Notch $g 900 132 1.65 @('working', 'attention') '1 needs you' -Bold
	Legend-Chips $g 900 236
}

function Variant-Left([string] $path, [string] $style) {
	$o = New-Bitmap $W $H; $g = $o.g
	Scroll-Background $g $W $H
	Right-Side $g

	switch ($style) {
		'L1' {   # two-tone black wordmark, one sentence, no list
			Left-Eyebrow $g 'DSH PLUGIN  ·  WINDOWS X64' 96
			Left-Wordmark $g 80 68 150 -Stacked | Out-Null
			$f = Font 21; $b = Solid (New-C '#9FB0C6')
			$g.DrawString('Every session is a bot. You see all of them at once.', $f, $b, 72, 366)
			$b.Dispose(); $f.Dispose()
			$f2 = Mono 14; $b2 = Solid (New-C '#5F7085')
			$g.DrawString('dsh plugin --profile desktop add dsh-agents-notch', $f2, $b2, 72, 470); $b2.Dispose(); $f2.Dispose()
		}
		'L2' {   # uppercase block, then the three states as bold-lead lines
			Left-Eyebrow $g 'A DEEPSEEK HARNESS PLUGIN' 96
			Left-Wordmark $g 62 68 148 -Stacked -Upper | Out-Null
			Left-StateLines $g 72 372 20
		}
		'L3' {   # hero wordmark, the sentence in one line underneath
			Left-Wordmark $g 92 68 140 -Stacked | Out-Null
			$f = Font 24 'Semibold'; $b = Solid (New-C '#B9C8DA')
			$g.DrawString('which session is working', $f, $b, 72, 396)
			$g.DrawString('which needs you, which is done', $f, $b, 72, 432)
			$b.Dispose(); $f.Dispose()
		}
		'L4' {   # numbered lines: the engineering facts, not the pitch
			Left-Eyebrow $g 'AGENTS NOTCH' 96
			Left-Wordmark $g 58 68 140 -Stacked | Out-Null
			Left-Numbered $g 72 340
		}
		'L5' {   # the legend itself moves left; the notch stands alone on the right
			Left-Wordmark $g 64 68 118 -Stacked | Out-Null
			Left-StatePills $g 72 320
		}
		'L6' {   # one statement, large: the tagline carries the meaning
			Left-Eyebrow $g 'ALWAYS ON TOP  ·  NO CONFIG REQUIRED' 96
			$f0 = Font-Black 46; $b0 = Solid (New-C '#F5F8FD')
			$g.DrawString('Your sessions,', $f0, $b0, 68, 150)
			$g.DrawString('on the glass.', $f0, $b0, 68, 202)
			$b0.Dispose(); $f0.Dispose()
			Left-Wordmark $g 34 68 296 -Stacked | Out-Null
			$f = Font 19; $b = Solid (New-C '#8FA0B5')
			$g.DrawString('one bot per session: working, needs you, done', $f, $b, 72, 396)
			$b.Dispose(); $f.Dispose()
		}
		'L7' {   # L3's wordmark with L2's three lines: the two things asked for, together
			Left-Eyebrow $g 'A DEEPSEEK HARNESS PLUGIN' 96
			Left-Wordmark $g 88 68 142 -Stacked | Out-Null
			Left-StateLines $g 72 380 20
		}
	}

	# the floor, same in every variant
	$hair = Solid ([System.Drawing.Color]::FromArgb(30, 255, 255, 255))
	$g.FillRectangle($hair, [single]72, [single]566, [single]1136, [single]1); $hair.Dispose()
	$ff = Mono 14; $fb = Solid (New-C '#54657A')
	$g.DrawString(("dsh-agents-notch  ·  v{0}" -f $pkgVersion), $ff, $fb, 72, 584)
	$right = 'mit  ·  github.com/iArxic/dsh-agents-notch'
	$g.DrawString($right, $ff, $fb, (1208 - (Text-Width $g $right $ff).Width), 584)
	$fb.Dispose(); $ff.Dispose()

	$g.Dispose(); $o.bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $o.bmp.Dispose()
}

$lefts = [ordered]@{ L1 = 'L1'; L2 = 'L2'; L3 = 'L3'; L4 = 'L4'; L5 = 'L5'; L6 = 'L6'; L7 = 'L7' }
foreach ($k in $lefts.Keys) {
	$file = Join-Path $OutDir "$k.png"
	Variant-Left $file $lefts[$k]
	"drew $file"
}

$rows = [Math]::Ceiling($lefts.Count / 3)
$sheet2 = [System.Drawing.Bitmap]::new(($W / 2) * 3 + 24, ($H / 2) * $rows + 18)
$sg2 = [System.Drawing.Graphics]::FromImage($sheet2)
$sg2.Clear([System.Drawing.Color]::FromArgb(255, 3, 4, 6))
$sg2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$i = 0
foreach ($k in $lefts.Keys) {
	$src = [System.Drawing.Image]::FromFile((Join-Path $OutDir "$k.png"))
	$x = 8 + ($i % 3) * ($W / 2 + 4); $y = 8 + [Math]::Floor($i / 3) * ($H / 2 + 4)
	$sg2.DrawImage($src, [int]$x, [int]$y, [int]($W / 2), [int]($H / 2))
	$label = Font 20 'Bold'
	$lb = Solid ([System.Drawing.Color]::FromArgb(255, 255, 255, 255))
	$sg2.DrawString($k, $label, $lb, [int]($x + 8), [int]($y + 6))
	$lb.Dispose(); $label.Dispose(); $src.Dispose()
	$i++
}
$sg2.Dispose()
$board2 = Join-Path $OutDir 'board-left.png'
$sheet2.Save($board2, [System.Drawing.Imaging.ImageFormat]::Png)
$sheet2.Dispose()
"contact sheet: $board2"
