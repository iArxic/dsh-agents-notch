<#
.SYNOPSIS
  Draw the repo's banner designs — four directions, one file each, plus a contact sheet.

.DESCRIPTION
  1280x640, the size a GitHub social preview wants, drawn with GDI+ so it renders identically on any
  machine that has Segoe UI. Re-run after an edit; nothing here touches the plugin or the window.

  The identity is the notch: a dark pill at the top of the screen holding one bot per session, each
  with its own rim colour, and a badge + ring that says what the session is DOING. Every variant keeps
  those two signals apart, because that is the one thing about this product that is easy to get wrong.

.EXAMPLE
  pwsh -File tools/design-banner.ps1
#>
[CmdletBinding()]
param([string] $OutDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'docs/design'))

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$W = 1280; $H = 640

# ── palette: the window's own tokens, so the banner and the product agree ────────────────────────
$ink       = [System.Drawing.Color]::FromArgb(255, 8, 10, 14)      # near-black
$inkSoft   = [System.Drawing.Color]::FromArgb(255, 18, 22, 30)
$panel     = [System.Drawing.Color]::FromArgb(238, 16, 20, 27)    # the notch's own body
$hairline  = [System.Drawing.Color]::FromArgb(70, 255, 255, 255)
$text      = [System.Drawing.Color]::FromArgb(255, 236, 242, 250)
$muted     = [System.Drawing.Color]::FromArgb(190, 190, 200, 214)
$teal      = [System.Drawing.Color]::FromArgb(255, 89, 211, 198)
$pink      = [System.Drawing.Color]::FromArgb(255, 232, 143, 196)
$amber     = [System.Drawing.Color]::FromArgb(255, 242, 179, 76)
$green     = [System.Drawing.Color]::FromArgb(255, 111, 208, 140)

function Linear-Brush($rect, $from, $to, $mode) {
	return [System.Drawing.Drawing2D.LinearGradientBrush]::new($rect, $from, $to, $mode)
}
function New-C($hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }

function New-Bitmap([int] $w, [int] $h) {
	$bmp = New-Object System.Drawing.Bitmap $w, $h
	$g = [System.Drawing.Graphics]::FromImage($bmp)
	$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
	$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
	$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
	return @{ bmp = $bmp; g = $g }
}

function Round-Rect([System.Drawing.RectangleF] $r, [single] $radius) {
	$p = New-Object System.Drawing.Drawing2D.GraphicsPath
	$d = $radius * 2
	$p.AddArc($r.X, $r.Y, $d, $d, 180, 90)
	$p.AddArc($r.Right - $d, $r.Y, $d, $d, 270, 90)
	$p.AddArc($r.Right - $d, $r.Bottom - $d, $d, $d, 0, 90)
	$p.AddArc($r.X, $r.Bottom - $d, $d, $d, 90, 90)
	$p.CloseFigure()
	return $p
}

function Fill-Round($g, $brush, [System.Drawing.RectangleF] $r, [single] $radius) {
	$p = Round-Rect $r $radius
	$g.FillPath($brush, $p)
	$p.Dispose()
}

function Stroke-Round($g, $pen, [System.Drawing.RectangleF] $r, [single] $radius) {
	$p = Round-Rect $r $radius
	$g.DrawPath($pen, $p)
	$p.Dispose()
}

function Font([single] $size, [string] $style = 'Regular') {
	# `[System.Drawing.FontStyle]::$variable` is not a thing PowerShell can evaluate, so the style is
	# mapped by name. Semibold is its own family on Windows, and reads better than bold at these sizes.
	if ($style -eq 'Semibold') {
		try { return [System.Drawing.Font]::new('Segoe UI Semibold', $size, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel) }
		catch { return [System.Drawing.Font]::new('Segoe UI', $size, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel) }
	}
	$fontStyle = if ($style -eq 'Bold') { [System.Drawing.FontStyle]::Bold } else { [System.Drawing.FontStyle]::Regular }
	return [System.Drawing.Font]::new('Segoe UI', $size, $fontStyle, [System.Drawing.GraphicsUnit]::Pixel)
}

function Mono([single] $size) {
	# one family per font, not a fallback list: GDI+ does not parse "A, B" and throws on the style
	foreach ($family in 'Cascadia Mono', 'Consolas') { try { return [System.Drawing.Font]::new($family, $size, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel) } catch { } }
	return [System.Drawing.Font]::new('Consolas', $size, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
}

function Text($g, [string] $s, $font, $brush, [single] $x, [single] $y) {
	$g.DrawString($s, $font, $brush, $x, $y)
}

function Text-Width($g, [string] $s, $font) { return $g.MeasureString($s, $font) }

function Solid($c) { return New-Object System.Drawing.SolidBrush $c }

<#
  A soft radial glow. The first version stacked 22 translucent ellipses and every ring showed as a
  band — a PathGradientBrush does the same job smoothly: a centre colour fading to transparent at the
  rim, one fill, no visible steps.
#>
function Glow($g, [single] $cx, [single] $cy, [single] $radius, $color, [int] $alpha = 70) {
	$rect = New-Object System.Drawing.RectangleF ($cx - $radius), ($cy - $radius), ($radius * 2), ($radius * 2)
	$path = New-Object System.Drawing.Drawing2D.GraphicsPath
	$path.AddEllipse($rect)
	$brush = [System.Drawing.Drawing2D.PathGradientBrush]::new($path)
	$brush.CenterColor = [System.Drawing.Color]::FromArgb($alpha, $color.R, $color.G, $color.B)
	$brush.SurroundColors = [System.Drawing.Color[]]@([System.Drawing.Color]::FromArgb(0, $color.R, $color.G, $color.B))
	$g.FillPath($brush, $path)
	$brush.Dispose(); $path.Dispose()
}

function Bg($g, [int] $w, [int] $h, $top, $bottom) {
	$rect = New-Object System.Drawing.RectangleF 0, 0, $w, $h
	$lg = Linear-Brush $rect $top $bottom ([System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
	$g.FillRectangle($lg, $rect)
	$lg.Dispose()
}

<#
  One bot: a pale disc with two eyes, a rim in the session's colour, and (optionally) the state badge.
  The rim is IDENTITY; the badge+ring is STATE. Never the same colour, or the banner teaches the wrong
  thing about the product.
#>
function Bot($g, [single] $cx, [single] $cy, [single] $r, $rim, [string] $state = 'working') {
	$face = New-Object System.Drawing.RectangleF ($cx - $r), ($cy - $r), ($r * 2), ($r * 2)
	$fill = Linear-Brush $face (New-C '#F2F7FF') (New-C '#CBD8EC') ([System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
	$g.FillEllipse($fill, $face)
	$fill.Dispose()
	$rimPen = New-Object System.Drawing.Pen $rim, ([single]([Math]::Max(1.6, $r * 0.10)))
	$g.DrawEllipse($rimPen, $face)
	$rimPen.Dispose()

	# eyes
	$eyeW = [single]($r * 0.17); $eyeH = [single]($r * 0.42)
	$eyeBrush = Solid (New-C '#2B3446')
	$dy = [single](-$r * 0.06)
	if ($state -eq 'done') {
		# a settled finish is an ARC, not a dot — the same face the window draws
		$pen = New-Object System.Drawing.Pen (New-C '#2B3446'), ([single]($r * 0.10))
		$g.DrawArc($pen, $cx - $r * 0.46, $cy + $dy - $r * 0.16, $r * 0.42, $r * 0.34, 200, 140)
		$g.DrawArc($pen, $cx + $r * 0.06, $cy + $dy - $r * 0.16, $r * 0.42, $r * 0.34, 200, 140)
		$pen.Dispose()
	} elseif ($state -eq 'attention') {
		# wide eyes: a session waiting on you
		$g.FillEllipse($eyeBrush, $cx - $r * 0.34, $cy + $dy - $r * 0.26, $r * 0.26, $r * 0.52)
		$g.FillEllipse($eyeBrush, $cx + $r * 0.10, $cy + $dy - $r * 0.26, $r * 0.26, $r * 0.52)
	} else {
		$g.FillEllipse($eyeBrush, $cx - $r * 0.34, $cy + $dy - $eyeH / 2, $eyeW, $eyeH)
		$g.FillEllipse($eyeBrush, $cx + $r * 0.18, $cy + $dy - $eyeH / 2, $eyeW, $eyeH)
	}
	$eyeBrush.Dispose()

	if ($state -ne 'working') {
		$badge = if ($state -eq 'done') { $green } else { $amber }
		$bx = $cx + $r * 0.58; $by = $cy + $r * 0.58; $br = $r * 0.34
		$b = Solid $badge
		$g.FillEllipse($b, $bx - $br, $by - $br, $br * 2, $br * 2)
		$b.Dispose()
		# the ring: state, drawn around the whole bot
		$ringPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(150, $badge.R, $badge.G, $badge.B)), ([single]([Math]::Max(1.2, $r * 0.07)))
		$g.DrawEllipse($ringPen, ($cx - $r - 3), ($cy - $r - 3), ($r + 3) * 2, ($r + 3) * 2)
		$ringPen.Dispose()
	}
}

<# The notch itself: the product's silhouette, at any scale. #>
function Notch($g, [single] $cx, [single] $top, [single] $scale = 1, [string[]] $states = @('working', 'attention', 'working'), [string] $label = '3 working') {
	$w = 300 * $scale; $h = 56 * $scale
	$rect = New-Object System.Drawing.RectangleF ($cx - $w / 2), $top, $w, $h
	$fill = New-Object System.Drawing.SolidBrush $panel
	Fill-Round $g $fill $rect ($h / 2)
	$fill.Dispose()
	$pen = New-Object System.Drawing.Pen $hairline, ([single]([Math]::Max(1, 1.2 * $scale)))
	Stroke-Round $g $pen $rect ($h / 2)
	$pen.Dispose()

	$rims = @($teal, $pink, $amber)
	$bx = $rect.X + $h * 0.44
	for ($i = 0; $i -lt $states.Count; $i++) {
		Bot $g ($bx + $i * $h * 0.42) ($rect.Y + $h / 2) ($h * 0.27) $rims[$i % 3] $states[$i]
	}
	$f = Font ([single](16 * $scale)) 'Semibold'
	$b = New-Object System.Drawing.SolidBrush $text
	$g.DrawString($label, $f, $b, ($rect.X + $h * 0.62 + $states.Count * $h * 0.42), ($rect.Y + $h / 2 - (11 * $scale)))
	$b.Dispose(); $f.Dispose()
	$cf = Font ([single](15 * $scale))
	$cb = New-Object System.Drawing.SolidBrush $muted
	$g.DrawString([char]0x2304, $cf, $cb, ($rect.Right - $h * 0.5), ($rect.Y + $h / 2 - (10 * $scale)))
	$cb.Dispose(); $cf.Dispose()
}

# ── A: the desktop shot — the notch at the top of a screen that is really working ────────────────
function Design-A([string] $path) {
	$o = New-Bitmap $W $H; $g = $o.g
	Bg $g $W $H (New-C '#0C1017') (New-C '#05070A')
	Glow $g 640 30 560 $teal 90

	# a suggestion of the desktop behind it: two window silhouettes, out of focus
	$win = New-Object System.Drawing.RectangleF 300, 210, 680, 430
	$wf = Solid ([System.Drawing.Color]::FromArgb(214, 22, 27, 36))
	Fill-Round $g $wf $win 18; $wf.Dispose()
	$wp = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(40, 255, 255, 255)), 1
	Stroke-Round $g $wp $win 18
	$bar = New-Object System.Drawing.RectangleF 300, 210, 680, 46
	$bf = Solid ([System.Drawing.Color]::FromArgb(224, 28, 34, 45))
	Fill-Round $g $bf $bar 16; $bf.Dispose()
	# three session rows, echoing the board
	$rows = @(
		@('Update code to match delta-bank response', $teal, 'working'),
		@('Ship the published exe', $pink, 'attention'),
		@('Improve DSH cache hit rate', $amber, 'done')
	)
	$y = 288
	foreach ($r in $rows) {
		$dot = Solid $r[1]
		$g.FillEllipse($dot, 336, ($y + 9), 9, 9); $dot.Dispose()
		Bot $g 372 ($y + 13) 15 $r[1] $r[2]
		$f = Font 17; $b = Solid $text
		$g.DrawString($r[0], $f, $b, 402, $y); $b.Dispose(); $f.Dispose()
		$y += 58
	}

	Notch $g 640 26 1.35 @('working', 'attention', 'done') '2 working'
	# wordmark + tagline
	$f1 = Font 54 'Semibold'; $b1 = Solid $text
	$g.DrawString('Agents Notch', $f1, $b1, 88, 420); $b1.Dispose(); $f1.Dispose()
	$f2 = Mono 19; $b2 = Solid $muted
	$g.DrawString('dsh-agents-notch  ·  Windows  ·  always on top', $f2, $b2, 92, 500); $b2.Dispose(); $f2.Dispose()
	$f3 = Font 21; $b3 = Solid (New-C '#AEBBCE')
	$g.DrawString('One bot per session: which is working, which needs you, which is done.', $f3, $b3, 92, 540); $b3.Dispose(); $f3.Dispose()

	$o.g.Dispose(); $o.bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $o.bmp.Dispose()
}

# ── B: the three states — the product as a legend, biggest and clearest ──────────────────────────
function Design-B([string] $path) {
	$o = New-Bitmap $W $H; $g = $o.g
	Bg $g $W $H (New-C '#0A0D13') (New-C '#06080C')
	Glow $g 200 180 330 $teal 80; Glow $g 640 240 340 $amber 70; Glow $g 1080 180 330 $green 75

	$f1 = Font 46 'Semibold'; $b1 = Solid $text
	$g.DrawString('Agents Notch', $f1, $b1, 76, 62); $b1.Dispose(); $f1.Dispose()
	$f2 = Mono 17; $b2 = Solid $muted
	$g.DrawString('a session board that fits above your work', $f2, $b2, 80, 120); $b2.Dispose(); $f2.Dispose()

	$cells = @(
		@('WORKING', $teal, 'breathing eyes, moving chevron', 'working'),
		@('NEEDS YOU', $amber, 'wide eyes, amber ring, badge', 'attention'),
		@('DONE', $green, 'a settled arc, and the count', 'done')
	)
	$x = 210
	foreach ($c in $cells) {
		$card = New-Object System.Drawing.RectangleF ($x - 140), 210, 280, 300
		$cf = Solid ([System.Drawing.Color]::FromArgb(255, 15, 19, 26))
		Fill-Round $g $cf $card 22; $cf.Dispose()
		$cp = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(34, 255, 255, 255)), 1
		Stroke-Round $g $cp $card 22; $cp.Dispose()
		Bot $g $x 316 56 $c[1] $c[3]
		$lf = Mono 17; $lb = Solid $c[1]
		$ms = Text-Width $g $c[0] $lf
		$g.DrawString($c[0], $lf, $lb, ($x - $ms.Width / 2), 400); $lb.Dispose(); $lf.Dispose()
		$df = Font 16; $db = Solid (New-C '#9AA8BC')
		$words = $c[2] -split ' '
		$line = ''; $ly = 434
		foreach ($word in $words) {
			$try = if ($line -eq '') { $word } else { "$line $word" }
			if ((Text-Width $g $try $df).Width -gt 210) { $m = Text-Width $g $line $df; $g.DrawString($line, $df, $db, ($x - $m.Width / 2), $ly); $ly += 22; $line = $word }
			else { $line = $try }
		}
		$m = Text-Width $g $line $df; $g.DrawString($line, $df, $db, ($x - $m.Width / 2), $ly)
		$db.Dispose(); $df.Dispose()
		$x += 430
	}
	Notch $g 640 552 0.92 @('working', 'attention', 'done') '2 working'
	$o.g.Dispose(); $o.bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $o.bmp.Dispose()
}

# ── C: terminal-adjacent — mono type, the notch as a diagram, the honest engineering tone ────────
function Design-C([string] $path) {
	$o = New-Bitmap $W $H; $g = $o.g
	Bg $g $W $H (New-C '#070A0F') (New-C '#0D1219')
	# a faint grid, so it reads as a hackable thing rather than a product shot
	$grid = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(12, 255, 255, 255)), 1
	for ($x = 0; $x -lt $W; $x += 64) { $g.DrawLine($grid, $x, 0, $x, $H) }
	for ($y = 0; $y -lt $H; $y += 64) { $g.DrawLine($grid, 0, $y, $W, $y) }
	$grid.Dispose()
	Glow $g 980 320 420 $teal 70

	# the install line with a cursor block: the one concrete thing a stranger can copy
	$f0 = Mono 15; $b0 = Solid (New-C '#6E7F94')
	$g.DrawString('PS C:\> dsh plugin --profile desktop add dsh-agents-notch', $f0, $b0, 72, 88); $b0.Dispose(); $f0.Dispose()
	$f1 = Font 60 'Semibold'; $b1 = Solid $text
	$g.DrawString('Agents', $f1, $b1, 68, 128)
	$g.DrawString('Notch', $f1, $b1, 68, 196); $b1.Dispose(); $f1.Dispose()
	$f2 = Font 20; $b2 = Solid (New-C '#9FB0C6')
	$g.DrawString('Which session is working.', $f2, $b2, 72, 300)
	$g.DrawString('Which one needs you.', $f2, $b2, 72, 330)
	$g.DrawString('Which are done.', $f2, $b2, 72, 360); $b2.Dispose(); $f2.Dispose()
	$f3 = Mono 15; $b3 = Solid (New-C '#5F7085')
	$g.DrawString('windows x64  ·  self-contained  ·  no .NET runtime', $f3, $b3, 72, 470); $b3.Dispose(); $f3.Dispose()

	Notch $g 900 150 1.5 @('working', 'attention') '1 needs you'
	# the state legend as a key, under it
	$key = @(@('working', $teal), @('needs you', $amber), @('done', $green))
	$kx = 700
	foreach ($k in $key) {
		$d = Solid $k[1]
		$g.FillEllipse($d, $kx, 320, 10, 10); $d.Dispose()
		$kf = Mono 15; $kb = Solid (New-C '#93A3B8')
		$g.DrawString($k[0], $kf, $kb, ($kx + 20), 314); $kb.Dispose(); $kf.Dispose()
		$kx += 180
	}
	# a small honest caption: the four-fact contract the notch is built on
	$f4 = Mono 14; $b4 = Solid (New-C '#4E5E72')
	$o.g.Dispose(); $o.bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $o.bmp.Dispose()
	# the floor: a hairline and the two facts that answer "should I install this?"
	# PointF, explicitly: the four-number overload mis-binds from PowerShell and throws
	# a filled 1px rectangle, not a Pen: the pen overloads mis-bind from PowerShell and $rule
	# ended up null, which surfaced as "Value cannot be null. (Parameter 'pen')"
	$hairBrush = Solid ([System.Drawing.Color]::FromArgb(30, 255, 255, 255))
	$g.FillRectangle($hairBrush, 72, 566, 1136, 1); $hairBrush.Dispose()
	$ff = Mono 14; $fb = Solid (New-C '#54657A')
	$g.DrawString('dsh-agents-notch  ·  v0.1.3', $ff, $fb, 72, 584)
	$right = 'mit  ·  github.com/iArxic/dsh-agents-notch'
	$g.DrawString($right, $ff, $fb, (1208 - (Text-Width $g $right $ff).Width), 584)
	$fb.Dispose(); $ff.Dispose()

}

# ── D: one line of type and the notch, restrained — closest to the DeepSeek banner's own tone ────
function Design-D([string] $path) {
	$o = New-Bitmap $W $H; $g = $o.g
	Bg $g $W $H (New-C '#0B1018') (New-C '#080B11')
	Glow $g 640 300 660 (New-C '#3D6F8C') 110
	# a horizon line, and the notch sitting on it like a hairline of UI across a screen
	$line = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(26, 255, 255, 255)), 1
	$g.DrawLine($line, 0, 200, $W, 200); $line.Dispose()

	Notch $g 640 168 1.6 @('working', 'attention') '1 working'
	$f1 = Font 66 'Semibold'; $b1 = Solid $text
	$s = 'Agents Notch'; $m = Text-Width $g $s $f1
	$g.DrawString($s, $f1, $b1, (640 - $m.Width / 2), 300); $b1.Dispose(); $f1.Dispose()
	$f2 = Font 24; $b2 = Solid (New-C '#A8B8CC')
	$s2 = 'which session is working, which needs you, which is done'; $m2 = Text-Width $g $s2 $f2
	$g.DrawString($s2, $f2, $b2, (640 - $m2.Width / 2), 392); $b2.Dispose(); $f2.Dispose()
	$f3 = Mono 16; $b3 = Solid (New-C '#66788E')
	$s3 = 'deepseek harness  ·  windows  ·  always on top'; $m3 = Text-Width $g $s3 $f3
	$g.DrawString($s3, $f3, $b3, (640 - $m3.Width / 2), 448); $b3.Dispose(); $f3.Dispose()

	$o.g.Dispose(); $o.bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $o.bmp.Dispose()
}

$designs = [ordered]@{ A = 'Design-A'; B = 'Design-B'; C = 'Design-C'; D = 'Design-D' }
foreach ($k in $designs.Keys) {
	$file = Join-Path $OutDir "$k.png"
	& $designs[$k] $file
	"drew $file"
}

# ── contact sheet: 2x2 at half size, so one look compares all four ───────────────────────────────
$sheet = New-Object System.Drawing.Bitmap ($W + 36), ($H + 36)
$sg = [System.Drawing.Graphics]::FromImage($sheet)
$sg.Clear([System.Drawing.Color]::FromArgb(255, 3, 4, 6))
$sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$i = 0
foreach ($k in $designs.Keys) {
	$src = [System.Drawing.Image]::FromFile((Join-Path $OutDir "$k.png"))
	$x = 12 + ($i % 2) * ($W / 2 + 6); $y = 12 + [Math]::Floor($i / 2) * ($H / 2 + 6)
	$sg.DrawImage($src, [int]$x, [int]$y, [int]($W / 2), [int]($H / 2))
	$label = Font 22 'Bold'
	$lb = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 255, 255))
	$sg.DrawString($k, $label, $lb, [int]($x + 10), [int]($y + 8))
	$lb.Dispose(); $label.Dispose(); $src.Dispose()
	$i++
}
$sg.Dispose()
$sheetPath = Join-Path $OutDir 'board.png'
$sheet.Save($sheetPath, [System.Drawing.Imaging.ImageFormat]::Png)
$sheet.Dispose()
"contact sheet: $sheetPath"
