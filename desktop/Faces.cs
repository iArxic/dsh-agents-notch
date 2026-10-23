// The bot's face vocabulary (round 12). One table says how each face sets the two eyes, and one
// builder draws the accents — brows, mouths, cheeks, symbols — that the eyes alone cannot say.
// Both bots read it: OrbView at 60 px on the board, MiniBotView at 16 px in the tab.
//
// The eyes stay the bots' own continuous bar ↔ arc geometry, so a blink or the finish squint never
// pops a shape into place. A face whose eyes are not bars (dots, ✕, hearts, stars, spirals) hides
// them and draws its own in the accent layer instead. Nothing is drawn OUTSIDE the face: thought
// dots and floating z's were tried, and at tab size they read as stray marks on the bots beside
// it (iArxic, 2026-10-23), so the face alone carries it. No blush either: the two faces that wore
// cheeks (proud, stuffed) were removed the same day.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AgentsNotch;

internal enum Face
{
    Idle, Focused, Thinking, Glances, Determined,
    Pleading, Peeking,
    Done, Starry, Wink, Love,
    Xx, Dizzy, Confused, Tired, Asleep,
}

internal enum EyeKind { Bar, Arc, None }

/// <summary>
/// How a face sets the eyes, on top of whatever the motion is doing: which shape each eye is, how
/// open (multiplied into the motion's own openness), how far apart, and a fixed gaze offset in
/// fractions of the face's diameter.
/// </summary>
internal readonly record struct FacePose(EyeKind Left, EyeKind Right, double Open = 1, double OpenR = 1,
    double Spread = 1, double DX = 0, double DY = 0);

/// <summary>What the accent builder hands back: the layer the accents are drawn in.</summary>
internal sealed class FaceAccent
{
    public readonly Canvas Layer = new() { IsHitTestVisible = false };
}

internal static class FaceArt
{
    static readonly Brush InkBrush = Frozen(Draw.C("#2A2A2E"));

    static SolidColorBrush Frozen(Color c, double a = 1)
    {
        var b = new SolidColorBrush(c) { Opacity = a };
        b.Freeze();
        return b;
    }

    public static FacePose PoseOf(Face face) => face switch
    {
        Face.Focused => new(EyeKind.Bar, EyeKind.Bar, .75, .75, DX: .03, DY: -.02),
        Face.Glances => new(EyeKind.Bar, EyeKind.Bar, DX: .06, DY: .02),
        Face.Determined => new(EyeKind.Bar, EyeKind.Bar, .8, .8, DY: .02),
        Face.Peeking => new(EyeKind.Bar, EyeKind.Bar, .45, .45, DY: .05),
        Face.Done => new(EyeKind.Arc, EyeKind.Arc),
        Face.Wink => new(EyeKind.Arc, EyeKind.Bar),
        Face.Confused => new(EyeKind.Bar, EyeKind.Bar, 1, .6),
        Face.Thinking or Face.Starry or Face.Love or Face.Xx or Face.Dizzy
            or Face.Tired or Face.Asleep => new(EyeKind.None, EyeKind.None),
        _ => new(EyeKind.Bar, EyeKind.Bar),
    };

    /// <summary>Parse a face name as the `--face` knob and the logs spell it: `xx`, `pleading`…</summary>
    public static bool TryParse(string? name, out Face face) =>
        Enum.TryParse(name?.Replace("-", "").Replace("^^", "done"), ignoreCase: true, out face)
        && !int.TryParse(name, out _) && Enum.IsDefined(face);

    /// <summary>
    /// The accents for a face, in the face's own box (0..s, origin at its top-left).
    ///
    /// `eyeY` is where the bot centres its eyes (a fraction of s), `thick` its eye stroke, and
    /// `compact` is the tab's 16 px bot: brows are dropped there (they collide with its taller eyes)
    /// and the eye-replacing shapes are drawn half again as large, so they still read as eyes.
    /// </summary>
    public static FaceAccent Build(Face face, double s, double eyeY, double thick, bool compact)
    {
        var a = new FaceAccent();
        var c = a.Layer;
        double cx = s / 2, y = eyeY * s, half = .215 * s;
        double L = cx - half, R = cx + half;
        double k = compact ? 1.5 : 1;
        double sw = Math.Max(thick, s * .05);

        void Stroke(string path, double w, Brush? ink = null) => c.Children.Add(new Path
        {
            Data = Geo(path), Stroke = ink ?? InkBrush, StrokeThickness = w,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        });
        void Fill(string path, Brush fill) => c.Children.Add(new Path { Data = Geo(path), Fill = fill });
        void FillG(Geometry g, Brush fill) => c.Children.Add(new Path { Data = g, Fill = fill });
        Ellipse Dot(double x, double yy, double r, Brush fill)
        {
            var e = new Ellipse { Width = r * 2, Height = r * 2, Fill = fill };
            c.Add(e, x - r, yy - r);
            return e;
        }
        void Mouth(double w) => Stroke(F("M{0},{1} h{2}", cx - w / 2, .72 * s, w), sw * .75);
        void Smile(double w, double depth) => Stroke(F("M{0},{1} Q{2},{3} {4},{1}", cx - w / 2, .66 * s, cx, .66 * s + depth, cx + w / 2), sw * .8);
        void Brows(string left, string right) { if (compact) return; Stroke(left, sw * .7); Stroke(right, sw * .7); }

        switch (face)
        {
            case Face.Focused:
                Mouth(.1 * s);
                break;
            case Face.Thinking:
                Dot(L + .04 * s, y - .06 * s, .045 * s * k, InkBrush);
                Dot(R + .04 * s, y - .06 * s, .045 * s * k, InkBrush);
                break;
            case Face.Determined:
                Brows(F("M{0},{1} l{2},{3}", L - .07 * s, y - .19 * s, .12 * s, .05 * s),
                      F("M{0},{1} l{2},{3}", R + .07 * s, y - .19 * s, -.12 * s, .05 * s));
                break;
            case Face.Pleading:
                Brows(F("M{0},{1} l{2},{3}", L - .06 * s, y - .22 * s, .11 * s, -.03 * s),
                      F("M{0},{1} l{2},{3}", R - .05 * s, y - .25 * s, .11 * s, .03 * s));
                break;
            case Face.Peeking:
                Stroke(F("M{0},{1} h{2} M{3},{1} h{2}", L - .07 * s, y - .02 * s, .14 * s, R - .07 * s), sw * .6);
                break;
            case Face.Starry:
                foreach (var x in new[] { L, R })
                    FillG(Scaled("M0,-9 L2.6,-2.8 9,-2.8 3.8,1.2 5.8,8 0,4 -5.8,8 -3.8,1.2 -9,-2.8 -2.6,-2.8 Z", x, y, s / 100 * k), InkBrush);
                Smile(.16 * s, .09 * s);
                break;
            case Face.Wink:
                Smile(.16 * s, .08 * s);
                break;
            case Face.Love:
                var heart = Frozen(Draw.C("#FF6B8A"));
                foreach (var x in new[] { L, R })
                    FillG(Scaled("M0,6 C-12,-4 -8,-14 0,-8 C8,-14 12,-4 0,6 Z", x, y, s / 100 * k), heart);
                Smile(.14 * s, .07 * s);
                break;
            case Face.Xx:
            {
                double d = .07 * s * k;
                Stroke(F("M{0},{1} L{2},{3} M{2},{1} L{0},{3} M{4},{1} L{5},{3} M{5},{1} L{4},{3}",
                    L - d, y - d, L + d, y + d, R - d, R + d), sw);
                break;
            }
            case Face.Dizzy:
                foreach (var x in new[] { L, R })
                {
                    double r = .06 * s * k;
                    Stroke(F("M{0},{1} A{2},{2} 0 1 1 {3},{4}", x - r, y, r, x, y + r), sw * .7);
                }
                break;
            case Face.Confused:
                Stroke(F("M{0},{1} q{2},{3} {4},0 t{4},0", cx - .08 * s, .72 * s, .04 * s, -.04 * s, .08 * s), sw * .7);
                break;
            case Face.Tired:
                foreach (var x in new[] { L, R })
                    Stroke(F("M{0},{1} Q{2},{3} {4},{1}", x - .075 * s * k, y, x, y + .08 * s * k, x + .075 * s * k), sw);
                Mouth(.1 * s);
                if (!compact)
                    Fill(F("M{0},{1} q{2},{3} 0,{4} q{5},{6} 0,{7} Z", cx + .36 * s, cx - .2 * s, .04 * s, .08 * s, .1 * s, -.04 * s, -.02 * s, -.1 * s),
                        Frozen(Draw.C("#8FC7FF")));
                break;
            case Face.Asleep:
                Stroke(F("M{0},{1} h{2} M{3},{1} h{2}", L - .08 * s * k, y, .16 * s * k, R - .08 * s * k), sw);
                break;
        }
        return a;
    }

    static string F(string format, params double[] args) =>
        string.Format(CultureInfo.InvariantCulture, format, Array.ConvertAll(args, v => (object)Math.Round(v, 3)));

    static Geometry Geo(string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    /// <summary>A symbol drawn in a 20-unit box, moved to (x, y) and scaled — hearts and stars.</summary>
    static Geometry Scaled(string data, double x, double y, double scale)
    {
        var g = Geometry.Parse(data).Clone();
        g.Transform = new TransformGroup { Children = { new ScaleTransform(scale, scale), new TranslateTransform(x, y) } };
        g.Freeze();
        return g;
    }
}
