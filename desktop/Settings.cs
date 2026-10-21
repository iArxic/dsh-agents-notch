// The human's settings, and the placement they imply.
//
// CONTRACT rule 4: `settings` belong to the host, not to a file the window invents. The window
// applies what it is given, CLAMPS it to the documented ranges, and remembers nothing on disk. A
// bad or missing value falls back to the documented default and is never allowed to reach the
// drawing code as NaN — a NaN in this window does not throw, it draws nothing at all, which is the
// one failure that looks like the feature was never built.
//
// Where each default comes from (docs/CONTRACT.md §`state.json`, schema 3):
//
//   scale           1.0      0.75 … 2.0 — the size of the whole surface
//   screen          primary  "primary" | "cursor" | "1" | "2" …  (1-based, as Windows numbers them)
//   anchor          top-center  "top-center" | "top-left" | "top-right"
//   maxRows         6        3 … 10
//   hideFullscreen  true     hide under a full-screen app

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace AgentsNotch;

internal sealed class Settings
{
    internal const double ScaleMin = .75, ScaleMax = 2.0;
    internal const int RowsMin = 3, RowsMax = 10;

    internal const string ScreenPrimary = "primary", ScreenCursor = "cursor";
    internal const string AnchorCenter = "top-center", AnchorLeft = "top-left", AnchorRight = "top-right";

    public double Scale = 1.0;
    public string Screen = ScreenPrimary;
    public string Anchor = AnchorCenter;
    public int MaxRows = 6;
    public bool HideFullscreen = true;

    public bool SameAs(Settings other) =>
        other is not null
        && Scale.Equals(other.Scale)
        && Screen == other.Screen
        && Anchor == other.Anchor
        && MaxRows == other.MaxRows
        && HideFullscreen == other.HideFullscreen;

    public override string ToString() =>
        $"scale={Scale.ToString("0.##", CultureInfo.InvariantCulture)} screen={Screen} anchor={Anchor}" +
        $" maxRows={MaxRows} hideFullscreen={HideFullscreen}";

    /// <summary>
    /// The settings block of a state file. A missing block, a missing member, a wrong type and an
    /// out-of-range number all resolve to the documented default — never to a throw and never to NaN.
    /// </summary>
    public static Settings Parse(JsonElement root)
    {
        var s = new Settings();
        if (root.ValueKind != JsonValueKind.Object) return s;
        if (!root.TryGetProperty("settings", out var block) || block.ValueKind != JsonValueKind.Object) return s;

        if (Number(block, "scale") is double scale) s.Scale = ClampScale(scale);
        if (String(block, "screen") is string screen) s.Screen = NormaliseScreen(screen);
        if (String(block, "anchor") is string anchor) s.Anchor = NormaliseAnchor(anchor);
        if (Number(block, "maxRows") is double rows) s.MaxRows = ClampRows(rows);
        if (block.TryGetProperty("hideFullscreen", out var hide)
            && (hide.ValueKind == JsonValueKind.True || hide.ValueKind == JsonValueKind.False))
            s.HideFullscreen = hide.GetBoolean();
        return s;
    }

    /// <summary>`0.75 … 2.0`, and anything that is not a finite number is the default 1.0.</summary>
    internal static double ClampScale(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, ScaleMin, ScaleMax) : 1.0;

    /// <summary>`3 … 10`, rounded, and anything unreadable is the default 6.</summary>
    internal static int ClampRows(double value) =>
        double.IsFinite(value) ? (int)Math.Clamp(Math.Round(value), RowsMin, RowsMax) : 6;

    /// <summary>"primary" / "cursor" / "1" / "2" …; anything else is "primary".</summary>
    internal static string NormaliseScreen(string value)
    {
        var text = (value ?? "").Trim().ToLowerInvariant();
        if (text == ScreenPrimary || text == ScreenCursor) return text;
        if (text.Length > 0 && text.All(char.IsAsciiDigit) && int.TryParse(text, out int n) && n >= 1) return n.ToString();
        return ScreenPrimary;
    }

    internal static string NormaliseAnchor(string value)
    {
        var text = (value ?? "").Trim().ToLowerInvariant();
        return text is AnchorCenter or AnchorLeft or AnchorRight ? text : AnchorCenter;
    }

    private static double? Number(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var v) ? v : null;

    private static string? String(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}

/// <summary>Where the window goes on the desk, in the units each API actually wants.</summary>
internal sealed record Placement(
    string Monitor,             // \\.\DISPLAY1
    Native.RECT ScreenPx,       // the monitor's whole rectangle, physical px
    Native.RECT WorkPx,         // …and its work area (taskbar excluded), physical px
    uint DpiMonitor,            // effective DPI of THAT monitor
    uint DpiSystem,             // the system DPI — WPF's own default, reported so a mismatch is visible
    double DpiScale,            // DpiMonitor / 96 — DIP → px for this window
    double WindowDipW, double WindowDipH,   // the window's size in DIP
    int WindowPxX, int WindowPxY,           // where SetWindowPos puts it, physical px
    int WindowPxW, int WindowPxH);

/// <summary>
/// Resolve `settings.screen` + `settings.anchor` + the window's DIP size into a concrete rectangle.
///
/// The same function answers the window's own placement and `--probe-rect`, which is the whole
/// point of the flag: a reviewer who cannot see this desk can still check where the notch lands.
/// </summary>
internal static class Place
{
    internal static Placement Resolve(Settings settings, double dipW, double dipH)
    {
        var displays = Native.Displays();
        var monitor = Choose(displays, settings.Screen);

        // No display at all: answer with a plausible screen instead of throwing. A window placed on
        // a rectangle that does not exist is still better than a process that died on startup.
        var screen = monitor?.Bounds ?? new Native.RECT { Right = 1920, Bottom = 1080 };
        var work = monitor?.Work ?? screen;

        uint dpiMonitor = monitor is null ? 0 : Native.DisplayDpi(monitor.Handle);
        uint dpiSystem = Native.SystemDpi();
        // The monitor's own DPI is what the window will be rendered at, so it is what sizes it.
        // Fall back to the system DPI (a system-aware process would) and only then to 1:1.
        double scale = dpiMonitor > 0 ? dpiMonitor / 96.0
            : dpiSystem > 0 ? dpiSystem / 96.0
            : 1.0;
        if (!double.IsFinite(scale) || scale <= 0) scale = 1.0;

        int pxW = (int)Math.Round(dipW * scale);
        int pxH = (int)Math.Round(dipH * scale);
        int workW = work.Right - work.Left;

        int x = settings.Anchor switch
        {
            Settings.AnchorLeft => work.Left,
            Settings.AnchorRight => work.Right - pxW,
            _ => work.Left + (workW - pxW) / 2,          // top-centre, the approved placement
        };
        int y = work.Top;                                 // it hangs from the top edge, always

        return new Placement(
            monitor?.Device ?? "", screen, work, dpiMonitor, dpiSystem, scale,
            dipW, dipH, x, y, pxW, pxH);
    }

    /// <summary>Which display `settings.screen` names — "primary", "cursor", or Windows' 1-based number.</summary>
    internal static Native.DisplayMonitor? Choose(List<Native.DisplayMonitor> displays, string mode)
    {
        if (displays.Count == 0) return null;

        if (mode == Settings.ScreenCursor)
        {
            var handle = Native.DisplayAt(Native.CursorPos());
            var under = displays.FirstOrDefault(d => d.Handle == handle);
            if (under is not null) return under;
        }
        else if (mode is not Settings.ScreenPrimary && int.TryParse(mode, out int number))
        {
            // Windows numbers displays by their device name, so `DISPLAY2` IS "2" in the display
            // settings. Only if nothing matches by name does the enumeration order get used, and
            // the caller's probe output names the device it actually chose.
            var named = displays.FirstOrDefault(d => d.Number == number);
            if (named is not null) return named;
            if (number >= 1 && number <= displays.Count) return displays[number - 1];
        }

        return displays.FirstOrDefault(d => d.Primary) ?? displays[0];
    }
}
