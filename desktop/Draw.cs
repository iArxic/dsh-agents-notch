// The approved design, as drawing code. These are the same helpers the mockups render
// (plugins/agent-overlay/designs/MockupStudio/Program.cs) — the notch draws the board the
// human signed off, not a lookalike.
//
// WPF colours are #AARRGGBB, not #RRGGBBAA: "#FFFFFF14" is an OPAQUE yellow-white. 8 % white
// is "#14FFFFFF". Getting this wrong put a khaki outline around every panel in round one.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace AgentsNotch;

internal static class Draw
{
    internal static readonly Color Ink = C("#EFEFEF"), Ink2 = C("#B9B9BE"), Mute = C("#7C7C82");
    internal static readonly Color Blue = C("#6AA8FF"), Amber = C("#FFC15E"), Green = C("#4ADE80"), Red = C("#FF6B6B"), Grey = C("#8E8E93");
    internal static readonly Color PanelBg = C("#161618"), Line = C("#14FFFFFF");

    // identity palette — one stable hue per session, independent of its state
    internal static readonly Color Teal = C("#6FD3C0"), Violet = C("#B49CFF"), Coral = C("#FFA07A"), Pink = C("#F2A6C8"), Lime = C("#A8D96B");

    // the tab's own palette — softer than the state dots, because these are 10 px words rather
    // than 6 px lights. Identity is carried by the little bots; this line carries the state.
    internal static readonly Color TabInk = C("#EDEDF0"), TabWord = C("#84848B"), TabDot = C("#4A4A52"),
                                   TabFlat = C("#C9C9CE"), TabGreen = C("#7ED7A2"), TabAmber = C("#F0C384"),
                                   TabRed = C("#EE9292");

    /// <summary>The tick that means "settled" everywhere else in this design.</summary>
    internal const string Tick = "\uE73E";

    internal static readonly FontFamily Ui = new("Segoe UI"), Glyphs = new("Segoe MDL2 Assets");

    internal static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;
    internal static SolidColorBrush B(Color c) => new(c);
    internal static SolidColorBrush B(Color c, double a) { var b = new SolidColorBrush(c); b.Opacity = a; return b; }

    static readonly Dictionary<Color, SolidColorBrush> BrushCache = new();

    /// <summary>
    /// One shared, frozen brush per colour. The run line re-colours itself on every wording change;
    /// handing it a fresh brush each time would allocate on a surface that updates every second.
    /// </summary>
    internal static SolidColorBrush Cached(Color c)
    {
        if (!BrushCache.TryGetValue(c, out var b))
        {
            b = B(c);
            b.Freeze();
            BrushCache[c] = b;
        }
        return b;
    }

    internal static void Add(this Canvas c, UIElement e, double x, double y)
    {
        Canvas.SetLeft(e, x); Canvas.SetTop(e, y); c.Children.Add(e);
    }

    internal static DropShadowEffect Shadow(int blur = 90, int depth = 30, double opacity = .34) => new()
    {
        Color = Colors.Black, BlurRadius = blur, ShadowDepth = depth, Direction = 270, Opacity = opacity,
    };

    internal static Border Panel(double w, double h, Color bg, double r, double alpha = 1, Color? border = null,
        double borderW = 1, DropShadowEffect? shadow = null)
    {
        var b = new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(r),
            Background = alpha >= 1 ? B(bg) : B(bg, alpha),
            BorderThickness = new Thickness(borderW),
            BorderBrush = border is null ? Brushes.Transparent : B(border.Value),
            SnapsToDevicePixels = true,
        };
        if (shadow is not null)
        {
            b.Effect = shadow;
            // A blurred shadow is the most expensive thing in the window, and a panel's is STATIC —
            // its content never changes, only things drawn on top of it do. Caching it keeps the blur
            // from being re-rasterised on every frame of the board's 77 fps motion (measured
            // 2026-10-21: the whole card cost ~2.5x a frame while it was open). WPF re-runs the cache
            // by itself whenever the cached content really does change.
        }
        return b;
    }

    internal static Grid Label(string text, double size, Color color, FontWeight? weight = null,
        HorizontalAlignment align = HorizontalAlignment.Left, FontFamily? font = null,
        double? width = null, double? height = null)
    {
        var tb = new TextBlock
        {
            Text = text, FontSize = size, Foreground = B(color), FontFamily = font ?? Ui,
            FontWeight = weight ?? FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = align, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var g = new Grid { Width = width ?? double.NaN, Height = height ?? double.NaN };
        g.Children.Add(tb);
        return g;
    }

    /// <summary>
    /// The ✕ that retires a settled row.
    ///
    /// A Grid with a transparent background, not a bare glyph: a TextBlock is only hit-testable on
    /// the pixels it actually inks, and a 9 px ✕ is a miserable target. The transparent brush makes
    /// the whole 14×14 square clickable.
    /// </summary>
    internal static Grid DismissMark()
    {
        var g = Label("\uE711", 9, Mute, font: Glyphs, width: 14, height: 14,
            align: HorizontalAlignment.Center);
        g.Background = Brushes.Transparent;
        g.Cursor = System.Windows.Input.Cursors.Hand;
        g.ToolTip = "Remove from the list — the same as ticking it in the app";
        return g;
    }

    /// <summary>
    /// A label as a plain TextBlock, so its TEXT can change without rebuilding the board.
    /// The tab's summary and the board's headline change every few seconds — they must not cost
    /// a rebuild of the whole board. Set <see cref="TextBlock.Width"/> to trim with an ellipsis.
    /// </summary>
    internal static TextBlock Text(string text, double size, Color color, FontWeight? weight = null)
    {
        return new TextBlock
        {
            Text = text, FontSize = size, Foreground = B(color), FontFamily = Ui,
            FontWeight = weight ?? FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
    }

    internal static Rectangle CatchLight(double w)
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        g.GradientStops.Add(new GradientStop(C("#00FFFFFF"), 0));
        g.GradientStops.Add(new GradientStop(C("#2EFFFFFF"), .18));
        g.GradientStops.Add(new GradientStop(C("#2EFFFFFF"), .82));
        g.GradientStops.Add(new GradientStop(C("#00FFFFFF"), 1));
        return new Rectangle { Width = w, Height = 1, Fill = g };
    }

    internal static Ellipse Dot(double d, Color c, double alpha = 1) => new() { Width = d, Height = d, Fill = B(c, alpha) };

    internal static Polyline Chevron(double size, Color c, double weight = 1.5) => new()
    {
        Points = [new Point(0, 0), new Point(size / 2, size / 2), new Point(size, 0)],
        Stroke = B(c), StrokeThickness = weight,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
    };

    internal static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    /// <summary>A task line is prose, not a table row: a state dot and the sentence.</summary>
    internal static Canvas TaskLine(string text, string kind, double w, bool dim = false, string tail = "")
    {
        var c = new Canvas { Width = w, Height = 18 };
        switch (kind)
        {
            case "active":
                c.Add(new Ellipse { Width = 13, Height = 13, Stroke = B(Blue, .30), StrokeThickness = 1 }, -1, 2.5);
                c.Add(Dot(7, Blue), 2, 5.5);
                break;
            case "attention":
                c.Add(new Ellipse { Width = 13, Height = 13, Stroke = B(Amber, .40), StrokeThickness = 1 }, -1, 2.5);
                c.Add(Dot(7, Amber), 2, 5.5);
                break;
            case "error":
                c.Add(new Ellipse { Width = 13, Height = 13, Stroke = B(Red, .35), StrokeThickness = 1 }, -1, 2.5);
                c.Add(Dot(7, Red), 2, 5.5);
                break;
            case "done":
                c.Add(Label("\uE73E", 10, Green, FontWeights.Bold, font: Glyphs, width: 12, height: 12), 1, 3);
                break;
            default:
                c.Add(new Ellipse { Width = 7, Height = 7, Stroke = B(Mute, .8), StrokeThickness = 1.1 }, 2, 5.5);
                break;
        }

        // A TAIL ("3 teammates") is a fact about the row that has to fit beside its title, so the
        // title gives way rather than the tail being clipped or overlapping it. This is the same
        // arithmetic the approved sheet used (round 08, S6).
        double tailW = 0;
        if (tail.Length > 0)
        {
            tailW = TextWidth("\u2009\u00b7\u2009" + tail, 9.5) + 4;
            double room = w - 18 - tailW;
            while (text.Length > 1 && TextWidth(text, 11.5) > room) text = text[..^2].TrimEnd() + "…";
        }

        var ink = dim ? Ink2 : kind is "active" or "attention" or "error" ? Ink : Ink2;
        c.Add(Label(text, 11.5, ink), 18, 0);
        if (tailW > 0)
            // The one tail left is a count of teammates, and it stays dim. A tail in the alarm tone used
            // to report a broken tool; Kuba had that removed outright on 2026-10-21, because a count
            // that never expires is not news (see Program.cs, R6).
            c.Add(Label("\u2009\u00b7\u2009" + tail, 9.5, RowTail), 18 + TextWidth(text, 11.5) + 2, 1.5);
        return c;
    }

    /// <summary>The words a row trails — dimmer than the title, brighter than nothing at all.</summary>
    internal static readonly Color RowTail = C("#6E6E77");

    /// <summary>How wide a string is at a size and weight, in the window's own typeface.</summary>
    internal static double TextWidth(string text, double size, FontWeight? weight = null)
    {
        var typeface = new Typeface(Ui, FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal);
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, size, Brushes.Black, 1.0);
        return formatted.WidthIncludingTrailingWhitespace;
    }

    /// <summary>A chip is its state: fill, dot and name all read the same way.</summary>
    internal static Canvas ChipName(string label, Color state, double w, double h)
    {
        var c = new Canvas { Width = w, Height = h };
        c.Children.Add(Panel(w, h, state, h / 2, .07));
        c.Add(Dot(6, state, .95), 12, h / 2 - 3);
        c.Add(Label(label, 10.5, state, FontWeights.SemiBold), 24, h / 2 - 8);
        return c;
    }

    internal static Canvas Notch(double width, double height, IReadOnlyList<Color> tones)
    {
        var c = new Canvas { Width = width, Height = height };
        var b = Panel(width, height, C("#08080A"), 0);
        b.CornerRadius = new CornerRadius(0, 0, height / 2, height / 2);
        b.Effect = Shadow(34, 8, .5);
        c.Children.Add(b);
        double x = (width - (tones.Count * 13 - 8)) / 2;
        foreach (var tone in tones) { c.Add(Dot(5, tone, .9), x, height / 2 - 2.5); x += 13; }
        return c;
    }
}

/// <summary>
/// The bot, live. The rim is the session (identity); the badge and the light are its state.
///
/// The eyes are ONE continuous thing. A bar and a `^` arc are drawn from a single geometry — the
/// arc's stroke is exactly the thickness of a closed bar, over exactly the same span — so bar →
/// closed → arc → closed → bar never pops a shape into place. The first version drew them as two
/// unrelated shapes and the seam was visible in the first GIF.
///
/// The RING around it is the board's one job: it appears when the session has settled — finished,
/// failed, or asking you something — and is absent while it is simply working. So "is anything
/// waiting for me?" is answerable from across the room, and a working bot stays quiet.
/// </summary>
internal sealed class OrbView : Canvas
{
    const double Off = 2.5;                       // room for the rim
    readonly double _size;
    readonly double _eyeW, _thick, _maxH, _cy, _gap;
    /// <summary>
    /// How far below the face's own bottom edge the state ring paints, blur included. 0 for a bot
    /// with no ring; read by the context readout, which has to stay out of the ring's way.
    /// </summary>
    public readonly double RingReach;
    readonly Ellipse _face, _aura, _ringBloom, _ringLine;
    readonly Rectangle _eyeL, _eyeR;
    readonly System.Windows.Shapes.Path _arcL, _arcR;
    readonly RotateTransform _tiltEyeL, _tiltEyeR, _tiltArcL, _tiltArcR;
    readonly SolidColorBrush _rimBrush, _badgeBrush, _ringBrush, _ringBloomBrush;
    readonly DropShadowEffect _rimGlow, _ringGlow;

    public OrbView(double size, Color state, Color identity)
    {
        _size = size;
        Width = size + Off * 2;
        Height = size + Off * 2;

        // the status aura — a soft halo in the session's STATE colour behind the face.
        // A blur that never changes shape and only ever moves its OPACITY: cache the raster, so the
        // per-frame motion costs a blend instead of a full blur pass.
        _aura = new Ellipse
        {
            Width = size * 2, Height = size * 2,
            Fill = new RadialGradientBrush(state, Colors.Transparent),
            Opacity = .28,
            Effect = new BlurEffect { Radius = size * .22 },
        };
        this.Add(_aura, Off - size / 2, Off - size / 2);

        // the state RING — hidden until the session settles. Two passes: a soft bloom and a crisp
        // line, so it glows rather than reading as a neon tube.
        double gap = size * .28, d1 = size + gap + 10, d2 = size + gap;
        double ringThick = Math.Max(4, size * .10);
        _ringBloomBrush = Draw.B(state, .20);
        _ringBrush = Draw.B(state, .95);
        _ringGlow = new DropShadowEffect { Color = state, BlurRadius = 22, ShadowDepth = 0, Opacity = .7 };
        _ringBloom = new Ellipse
        {
            Width = d1, Height = d1, Stroke = _ringBloomBrush,
            StrokeThickness = ringThick, Effect = new BlurEffect { Radius = 8 }, Opacity = 0,
        };
        _ringLine = new Ellipse
        {
            Width = d2, Height = d2, Stroke = _ringBrush, StrokeThickness = 2,
            Effect = _ringGlow, Opacity = 0,
        };
        this.Add(_ringBloom, Off + size / 2 - d1 / 2, Off + size / 2 - d1 / 2);
        this.Add(_ringLine, Off + size / 2 - d2 / 2, Off + size / 2 - d2 / 2);
        // How far the ring paints BELOW the face's own edge, blur included. The context readout hangs
        // there, and a session that has settled grows a ring that used to run straight through it — so
        // the readout asks for this number instead of keeping a second copy of the geometry.
        RingReach = (d1 + ringThick) / 2 + 8 - size / 2;

        _rimBrush = Draw.B(identity);
        _rimGlow = new DropShadowEffect { Color = identity, BlurRadius = 20, ShadowDepth = 0, Opacity = .6 };
        Children.Add(new Ellipse
        {
            Width = size + 5, Height = size + 5, Stroke = _rimBrush, StrokeThickness = 2.5, Effect = _rimGlow,
        });

        _face = new Ellipse
        {
            Width = size, Height = size,
            Fill = new RadialGradientBrush(Draw.C("#FFFFFF"), Draw.C("#D8E4FF")) { GradientOrigin = new Point(.38, .3) },
            Effect = new DropShadowEffect { Color = Draw.C("#7C9BE0"), BlurRadius = size * .4, ShadowDepth = 0, Opacity = .45 },
        };
        this.Add(_face, Off, Off);

        // One geometry for both eye shapes: the eye's centre never moves, and a closed bar is a
        // capsule of `_thick`, which is exactly what a zero-bend arc draws.
        _eyeW = size * .10; _thick = Math.Max(2, size * .033); _maxH = size * .26;
        _cy = size * .34 + _maxH / 2; _gap = size * .215;
        var ink = Draw.B(Draw.C("#2A2A2E"));
        _eyeL = new Rectangle { Width = _eyeW, Height = _maxH, RadiusX = _eyeW / 2, RadiusY = _eyeW / 2, Fill = ink };
        _eyeR = new Rectangle { Width = _eyeW, Height = _maxH, RadiusX = _eyeW / 2, RadiusY = _eyeW / 2, Fill = ink };
        Children.Add(_eyeL); Children.Add(_eyeR);

        _arcL = ArcPath(ink);
        _arcR = ArcPath(ink);
        _arcL.StrokeThickness = _thick;
        _arcR.StrokeThickness = _thick;
        Children.Add(_arcL); Children.Add(_arcR);

        // The tilt rotations are made ONCE and their angle is what changes: `Set` runs on every
        // compositor frame, and a new `RotateTransform` per frame (what this did until 2026-10-21)
        // re-assigns `RenderTransform` on four elements 77 times a second.
        _tiltEyeL = new RotateTransform(0);
        _tiltEyeR = new RotateTransform(0);
        _tiltArcL = new RotateTransform(0);
        _tiltArcR = new RotateTransform(0);
        var centred = new Point(.5, .5);
        foreach (var (face, tilt) in new (UIElement, RotateTransform)[] { (_eyeL, _tiltEyeL), (_eyeR, _tiltEyeR), (_arcL, _tiltArcL), (_arcR, _tiltArcR) })
        {
            face.RenderTransformOrigin = centred;
            face.RenderTransform = tilt;
        }

        double bs = size * .3;
        var badge = new Canvas { Width = bs, Height = bs };
        _badgeBrush = Draw.B(state);
        badge.Children.Add(new Ellipse { Width = bs, Height = bs, Fill = _badgeBrush, Stroke = Draw.B(Draw.PanelBg), StrokeThickness = bs * .14 });
        badge.Children.Add(new Ellipse { Width = bs * .34, Height = bs * .34, Fill = Draw.B(Colors.White) });
        this.Add(badge, Off + size * .72, Off + size * .72);
    }

    /// <summary>
    /// Re-colour the bot in place: the badge and the aura take the STATE, the rim the IDENTITY.
    /// The window polls the state file every 300 ms — the orb must not be rebuilt for a colour.
    /// </summary>
    public void SetColours(Color state, Color identity)
    {
        _aura.Fill = new RadialGradientBrush(state, Colors.Transparent);
        _rimBrush.Color = identity;
        _rimGlow.Color = identity;
        _badgeBrush.Color = state;
        _ringBrush.Color = state;
        _ringBloomBrush.Color = state;
        _ringGlow.Color = state;
    }

    static System.Windows.Shapes.Path ArcPath(Brush ink) => new()
    {
        Stroke = ink, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        Visibility = Visibility.Collapsed,
    };

    /// <summary>`M(0,0) Q(w/2,-bend) (w,0)` — at bend 0 this IS the closed eye.</summary>
    static PathGeometry ArcGeometry(double w, double bend)
    {
        var figure = new PathFigure { StartPoint = new Point(0, 0) };
        figure.Segments.Add(new QuadraticBezierSegment(new Point(w / 2, -Math.Max(0.01, bend)), new Point(w, 0), true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    /// <summary>
    /// open 0..1.4 (over 1 is a wide, alert eye) · lookX/lookY -1..1 drift · rim 0..1 glows ·
    /// `arc` swaps the bars for `^ ^` and `bend` raises it out of the flat line.
    /// </summary>
    public void Set(double open, double lookX, double lookY, double rim, double glow = .3,
        bool arc = false, double bend = 0, double spread = 1, double tilt = 0)
    {
        bool bars = !arc;
        _eyeL.Visibility = _eyeR.Visibility = bars ? Visibility.Visible : Visibility.Collapsed;
        _arcL.Visibility = _arcR.Visibility = bars ? Visibility.Collapsed : Visibility.Visible;

        double half = _gap * spread;
        double shift = lookX * _size * .02;
        if (bars)
        {
            double h = Math.Max(_thick, _maxH * open);
            double top = Off + _cy - h / 2 + lookY * _size * .03;
            _tiltEyeL.Angle = -tilt;
            _tiltEyeR.Angle = tilt;
            _eyeL.Height = _eyeR.Height = h;
            _eyeL.RadiusY = _eyeR.RadiusY = Math.Min(_eyeW / 2, h / 2);
            Canvas.SetTop(_eyeL, top);
            Canvas.SetTop(_eyeR, top);
            Canvas.SetLeft(_eyeL, Off + _size / 2 - half - _eyeW / 2 + shift);
            Canvas.SetLeft(_eyeR, Off + _size / 2 + half - _eyeW / 2 + shift);
        }
        else
        {
            double aw = Math.Max(_thick, _eyeW - _thick);
            double pixels = _size * .055 * bend;
            _arcL.Data = ArcGeometry(aw, pixels);
            _arcR.Data = ArcGeometry(aw, pixels);
            _tiltArcL.Angle = -tilt;
            _tiltArcR.Angle = tilt;
            double y = Off + _cy + lookY * _size * .03;
            Canvas.SetTop(_arcL, y); Canvas.SetTop(_arcR, y);
            Canvas.SetLeft(_arcL, Off + _size / 2 - half - aw / 2 + shift);
            Canvas.SetLeft(_arcR, Off + _size / 2 + half - aw / 2 + shift);
        }

        _rimBrush.Opacity = .55 + .45 * rim;
        // The glow breathes through its OPACITY, not its blur radius: a `BlurRadius` that changes every
        // frame forces the effect to re-render its whole surface every frame, which is the most
        // expensive thing this window could do at 77 fps (measured 2026-10-21, `--no-effects` swung the
        // CPU cost by less than a fifth while a per-frame radius change re-blurs 82 500 px).
        _rimGlow.Opacity = .28 + .52 * rim;
        _aura.Opacity = glow;
    }

    /// <summary>The ring's presence: 0 hidden, 1 fully drawn · `pulse` moves it while it wants you.</summary>
    public void SetRing(double alpha, double pulse = 1)
    {
        double a = Math.Clamp(alpha * pulse, 0, 1);
        _ringLine.Opacity = a;
        _ringBloom.Opacity = a * .9;
    }

    public double Size => _size;
    public double CenterOffset => Off + _size / 2;
}

/// <summary>
/// A bot small enough for the tab, wearing the big bot's own vocabulary: the session's colour as
/// the rim, its state as the aura, and — once the session has SETTLED — the state's ring and the
/// state's face. Working bots keep the plain blink and no ring; finished ones squint, failed ones
/// narrow and tilt, and a question puts the ring on.
///
/// M1 of round 08, picked by the human. The ring is the same idea as OrbView's, one quarter the
/// size: a soft bloom and a crisp line in the state's colour, OUTSIDE the identity rim, so the bot
/// still says which session it is. The proportions are the big bot's (a gap of ~0.28 of the face's
/// diameter), which is why the padding is 7 and not the 3 the aura alone would need.
/// </summary>
internal sealed class MiniBotView : Canvas
{
    const double Off = 7;                    // room for the ring outside the rim
    readonly double _size, _eyeW, _baseY, _maxH, _thick, _gap;
    readonly Ellipse _aura, _rim;
    readonly System.Windows.Shapes.Path _ringBloom, _ringLine;
    readonly double _ringCirc;
    double _sweep = -1;                      // ring drawn so far, 0..1; -1 = not driven yet
    readonly Rectangle _eyeL, _eyeR;
    readonly System.Windows.Shapes.Path _arcL, _arcR;
    readonly RotateTransform _tiltEyeL, _tiltEyeR;
    readonly PathGeometry _arcGeo;
    readonly SolidColorBrush _ringLineBrush, _ringBloomBrush;

    public MiniBotView(double size, Color state, Color identity)
    {
        _size = size;
        Width = size + Off * 2;
        Height = size + Off * 2;

        _aura = new Ellipse
        {
            Width = size * 2.1, Height = size * 2.1,
            Fill = new RadialGradientBrush(state, Colors.Transparent),
            Opacity = .3,
            Effect = new BlurEffect { Radius = size * .3 },
        };
        this.Add(_aura, Off - size * .55, Off - size * .55);

        // THE STATE RING, DRAWN RATHER THAN SHOWN (round 09, motion A).
        //
        // A ring that appears says "this session is settled"; a ring that CLOSES says "it just
        // finished", which is the thing the human was waiting for. So the ring is a dashed stroke
        // whose dash is exactly one circumference: `StrokeDashOffset` walks the dash off the circle,
        // and the whole animation is one number — no geometry is rebuilt per frame.
        double d1 = size + 10, d2 = size + 8;
        _ringBloomBrush = Draw.B(state, .22);
        _ringLineBrush = Draw.B(state, .95);
        _ringCirc = Math.PI * d2;
        var centre = new Point(Off + size / 2, Off + size / 2);
        _ringBloom = new System.Windows.Shapes.Path
        {
            Data = new EllipseGeometry(centre, d1 / 2, d1 / 2),
            Stroke = _ringBloomBrush, StrokeThickness = 2.4,
            Effect = new BlurEffect { Radius = 2.5 }, Opacity = 0,
            RenderTransform = new RotateTransform(-90, centre.X, centre.Y),
        };
        _ringLine = new System.Windows.Shapes.Path
        {
            Data = new EllipseGeometry(centre, d2 / 2, d2 / 2),
            Stroke = _ringLineBrush, StrokeThickness = 1.1, Opacity = 0,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            RenderTransform = new RotateTransform(-90, centre.X, centre.Y),
        };
        // dash units are multiples of the stroke's thickness, so one circumference is `_ringCirc /
        // thickness` — that is the whole dash array, and the offset starts it fully hidden
        _ringLine.StrokeDashArray = new DoubleCollection { _ringCirc / 1.1, _ringCirc / 1.1 };
        _ringLine.StrokeDashOffset = _ringCirc / 1.1;
        Children.Add(_ringBloom);
        Children.Add(_ringLine);

        this.Add(new Ellipse
        {
            Width = size, Height = size,
            Fill = new RadialGradientBrush(Draw.C("#FFFFFF"), Draw.C("#D8E4FF")) { GradientOrigin = new Point(.38, .3) },
        }, Off, Off);

        _rim = new Ellipse
        {
            Width = size, Height = size, Stroke = Draw.B(identity, .95),
            StrokeThickness = Math.Max(1.2, size * .11),
        };
        this.Add(_rim, Off, Off);

        // the eyes, as one continuous shape: a bar that closes into a line and rises into `^`
        _eyeW = size * .17; _maxH = size * .30; _baseY = size * .30;
        _thick = Math.Max(1, size * .10);
        _gap = size * .215;
        var ink = Draw.B(Draw.C("#2A2A2E"));
        _eyeL = new Rectangle { Width = _eyeW, Height = _maxH, RadiusX = _eyeW / 2, RadiusY = _eyeW / 2, Fill = ink };
        _eyeR = new Rectangle { Width = _eyeW, Height = _maxH, RadiusX = _eyeW / 2, RadiusY = _eyeW / 2, Fill = ink };
        this.Add(_eyeL, 0, 0);
        this.Add(_eyeR, 0, 0);

        _arcL = ArcPath(ink);
        _arcR = ArcPath(ink);
        _arcL.StrokeThickness = _thick;
        _arcR.StrokeThickness = _thick;
        this.Add(_arcL, 0, 0);
        this.Add(_arcR, 0, 0);

        // Both built ONCE: `Set` runs on every compositor frame, and a finished bot keeps its `^ ^`
        // arcs for as long as it sits on the tab — so this used to rebuild two geometries and two
        // rotations per frame, per bot, forever.
        _arcGeo = ArcGeometry(Math.Max(_thick, _eyeW - _thick), size * .085);
        _arcL.Data = _arcGeo;
        _arcR.Data = _arcGeo;
        _tiltEyeL = new RotateTransform(0);
        _tiltEyeR = new RotateTransform(0);
        var centred = new Point(.5, .5);
        foreach (var (face, tilt) in new (UIElement, RotateTransform)[] { (_eyeL, _tiltEyeL), (_eyeR, _tiltEyeR) })
        {
            face.RenderTransformOrigin = centred;
            face.RenderTransform = tilt;
        }
    }

    static System.Windows.Shapes.Path ArcPath(Brush ink) => new()
    {
        Stroke = ink, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        Visibility = Visibility.Collapsed,
    };

    /// <summary>`M(0,0) Q(w/2,-bend) (w,0)` — at bend 0 this IS the closed eye.</summary>
    internal static PathGeometry ArcGeometry(double w, double bend)
    {
        var figure = new PathFigure { StartPoint = new Point(0, 0) };
        figure.Segments.Add(new QuadraticBezierSegment(new Point(w / 2, -Math.Max(0.01, bend)), new Point(w, 0), true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    /// <summary>
    /// The bot's face and ring for a session's state.
    ///
    /// `blink` 0..1 is the working blink; a settled bot does not blink, because its face IS the
    /// state — a finished bot that blinks out of its squint reads as a bot that was never finished.
    /// </summary>
    public void Set(double blink, double glow, string status = "working")
    {
        bool settled = status is "done" or "error" or "attention";
        bool arc = status == "done";
        double open = status switch
        {
            "done" => 0,          // the `^ ^` squint the big bot settles into
            "attention" => 1.36,  // wide eyes — the same widened rest the big bot holds
            "error" => .42,       // half shut, and tilted out with it
            _ => 1 - blink,
        };
        double spread = status == "attention" ? 1.16 : 1;
        double tilt = status == "error" ? 16 : 0;
        double cy = _baseY + _maxH / 2;
        double half = _gap * spread;

        _eyeL.Visibility = _eyeR.Visibility = arc ? Visibility.Collapsed : Visibility.Visible;
        _arcL.Visibility = _arcR.Visibility = arc ? Visibility.Visible : Visibility.Collapsed;

        if (arc)
        {
            _arcL.Data = _arcGeo;
            _arcR.Data = _arcGeo;
            double aw = Math.Max(_thick, _eyeW - _thick);
            Canvas.SetLeft(_arcL, Off + _size / 2 - half - aw / 2);
            Canvas.SetLeft(_arcR, Off + _size / 2 + half - aw / 2);
            Canvas.SetTop(_arcL, Off + cy - _size * .03);
            Canvas.SetTop(_arcR, Off + cy - _size * .03);
        }
        else
        {
            double h = Math.Max(_thick, _maxH * open);
            double top = Off + cy - h / 2;
            _eyeL.Height = _eyeR.Height = h;
            _eyeL.RadiusY = _eyeR.RadiusY = Math.Min(_eyeW / 2, h / 2);
            _tiltEyeL.Angle = -tilt;
            _tiltEyeR.Angle = tilt;
            Canvas.SetTop(_eyeL, top);
            Canvas.SetTop(_eyeR, top);
            Canvas.SetLeft(_eyeL, Off + _size / 2 - half - _eyeW / 2);
            Canvas.SetLeft(_eyeR, Off + _size / 2 + half - _eyeW / 2);
        }

        if (_sweep < 0) SetSweep(settled ? 1 : 0);   // first call, or a state the window has not animated
        _aura.Opacity = glow;
    }

    /// <summary>
    /// How much of the ring is drawn, 0..1. The window drives this over 360 ms when a session
    /// settles; a still frame needs only the endpoints.
    /// </summary>
    public void SetSweep(double p)
    {
        _sweep = Math.Clamp(p, 0, 1);
        _ringLine.Opacity = _sweep > 0 ? 1 : 0;
        _ringLine.StrokeDashOffset = (1 - _sweep) * _ringCirc / 1.1;
        _ringBloom.Opacity = _sweep * .9;
    }

    /// <summary>
    /// Re-colour the little bot in place: the aura and the ring take the session's STATE, the rim
    /// its IDENTITY.
    /// </summary>
    public void SetColours(Color state, Color identity)
    {
        _aura.Fill = new RadialGradientBrush(state, Colors.Transparent);
        _ringLineBrush.Color = state;
        _ringBloomBrush.Color = state;
        _rim.Stroke = Draw.B(identity, .95);
    }
}

/// <summary>One run of the tab's summary line: some words, and how they are set.</summary>
internal readonly record struct TabRun(string Text, double Size, Color Colour, FontWeight Weight, bool Glyph = false);

/// <summary>
/// The tab's one line, set as runs instead of one string.
///
/// The summary holds a heavy count, a dim word and a green tick — two weights and up to four
/// colours, which a single TextBlock cannot express. It is reconciled in place rather than rebuilt:
/// the wording changes whenever a session starts or finishes, and rebuilding the tab (let alone the
/// board) must not be what that costs.
///
/// A horizontal StackPanel puts every run on its own baseline; with 9–11 px runs that is invisible,
/// so each run is centred in the line instead and the line as a whole is centred on the tab's
/// optical middle by Program.LayoutTab.
/// </summary>
internal sealed class RunLine : StackPanel
{
    readonly List<TextBlock> _runs = new();

    public RunLine()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public void Set(IReadOnlyList<TabRun> runs)
    {
        while (_runs.Count > runs.Count)
        {
            Children.RemoveAt(_runs.Count - 1);
            _runs.RemoveAt(_runs.Count - 1);
        }
        while (_runs.Count < runs.Count)
        {
            var added = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.None,
            };
            _runs.Add(added);
            Children.Add(added);
        }

        for (int i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            var tb = _runs[i];
            if (tb.Text != run.Text) tb.Text = run.Text;
            if (tb.FontSize != run.Size) tb.FontSize = run.Size;
            if (!Equals(tb.FontWeight, run.Weight)) tb.FontWeight = run.Weight;
            var font = run.Glyph ? Draw.Glyphs : Draw.Ui;
            if (!Equals(tb.FontFamily, font)) tb.FontFamily = font;
            tb.Foreground = Draw.Cached(run.Colour);
        }
    }

    /// <summary>The width the runs need — what sizes the pill.</summary>
    public double LineWidth()
    {
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return DesiredSize.Width;
    }

    public double LineHeight()
    {
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return DesiredSize.Height;
    }
}

