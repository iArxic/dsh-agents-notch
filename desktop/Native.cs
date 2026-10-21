// The Win32 half of the notch: topmost, no focus theft, "is something full-screen on the display
// right now", where the pointer is, and — since placement became a setting — which monitor to sit
// on and what that monitor's DPI is. Everything here is a measured decision.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AgentsNotch;

internal static class Native
{
    // ── structures ───────────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    /// <summary>MONITORINFO with the device name — the one the human's display settings numbers.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    internal delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    // ── imports ─────────────────────────────────────────────────────────────────
    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int idx, IntPtr v);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr h, int idx, int v);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);

    // ── constants ───────────────────────────────────────────────────────────────
    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_NOACTIVATE = 0x08000000;
    internal const int WS_EX_LAYERED = 0x00080000;

    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_NOOWNERZORDER = 0x0200;

    internal const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    internal const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    internal const int QUNS_NOT_PRESENT = 1;
    internal const int QUNS_BUSY = 2;
    internal const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    internal const int QUNS_PRESENTATION_MODE = 4;

    // ── helpers ─────────────────────────────────────────────────────────────────

    /// <summary>Per-monitor DPI awareness, before any window exists.</summary>
    internal static void EnablePerMonitorDpi()
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { /* XP-era fallback is fine */ }
    }

    /// <summary>Tool window, never activates, stays layered — and back on top.</summary>
    internal static void MakeTopmostAndInert(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        var ex = GetWindowLong(h, GWL_EXSTYLE);
        SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED);
        SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    /// <summary>Z-order is not a guarantee — re-assert it about once a second.</summary>
    internal static void ReassertTopmost(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    internal static bool ExcludeFromCapture(Window w)
    {
        try { return SetWindowDisplayAffinity(new WindowInteropHelper(w).Handle, WDA_EXCLUDEFROMCAPTURE); }
        catch { return false; }
    }

    internal static long GetWindowLong(IntPtr h, int index)
    {
        if (IntPtr.Size == 8) return SetWindowLongPtr(h, index, IntPtr.Zero).ToInt64();
        return SetWindowLong(h, index, 0);
    }

    internal static void SetWindowLong(IntPtr h, int index, long value)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr(h, index, new IntPtr(value));
        else SetWindowLong(h, index, (int)value);
    }

    internal static POINT CursorPos() { GetCursorPos(out var p); return p; }

    internal static RECT WindowRect(IntPtr h) { GetWindowRect(h, out var r); return r; }

    /// <summary>
    /// The shell's own notification state: 1 not present, 2 busy (a full-screen app has the
    /// display — modern games report THIS, not 3), 3 D3D full screen, 4 presentation.
    /// </summary>
    internal static int UserNotificationState() { SHQueryUserNotificationState(out var s); return s; }

    /// <summary>True when the foreground window covers its whole monitor (within ±2 px).</summary>
    internal static bool ForegroundIsFullscreen()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;

        RECT r;
        if (DwmGetWindowAttribute(fg, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf<RECT>()) != 0)
            GetWindowRect(fg, out r);

        var mon = MonitorFromWindow(fg, 2 /* MONITOR_DEFAULTTONEAREST */);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return false;

        const int tol = 2;
        return r.Left <= mi.rcMonitor.Left + tol && r.Top <= mi.rcMonitor.Top + tol
            && r.Right >= mi.rcMonitor.Right - tol && r.Bottom >= mi.rcMonitor.Bottom - tol;
    }

    /// <summary>The work area of the monitor the window is on (taskbar excluded).</summary>
    internal static RECT WorkArea(IntPtr h)
    {
        var mon = MonitorFromWindow(h, 2);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(mon, ref mi)) return mi.rcWork;
        return new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    internal static void Move(IntPtr h, int x, int y)
        => SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);

    // ── the display's own refresh rate ────────────────────────────────────────────
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int index);

    /// <summary>
    /// The primary display's refresh rate in Hz (GDI `VREFRESH`), or 0 when it cannot be read.
    ///
    /// A frame budget is meaningless without it: this window animates from a 16 ms `DispatcherTimer`,
    /// which is a 60 Hz assumption baked into a constant, while the panel may be 120 Hz or 144 Hz — in
    /// which case the timer is the ceiling and no animation on top of it can be smooth.
    /// </summary>
    internal static int RefreshHz()
    {
        try
        {
            var dc = GetDC(IntPtr.Zero);
            int hz = GetDeviceCaps(dc, 116 /* VREFRESH */);
            ReleaseDC(IntPtr.Zero, dc);
            return hz > 1 ? hz : 0;
        }
        catch { return 0; }
    }

    // ── monitors and DPI ─────────────────────────────────────────────────────────
    //
    // `settings.screen` has to mean what Windows' own display settings mean by "1" and "2", so the
    // monitors are enumerated HERE rather than through WinForms' `Screen` class: GetMonitorInfo
    // hands back the device name (`\\.\DISPLAY1`) that the numbering comes from, and the monitor
    // rectangles are physical pixels whatever the process's DPI awareness — which is exactly what
    // `SetWindowPos` takes. The per-monitor DPI comes from the monitor handle itself, so a window
    // placed on the OTHER monitor is sized against THAT monitor's scale.

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfoEx(IntPtr h, ref MONITORINFOEX mi);
    [DllImport("user32.dll", EntryPoint = "MonitorFromPoint")] private static extern IntPtr MonitorFromPointW(POINT p, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr h, int type, out uint x, out uint y);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();

    /// <summary>One attached display: its handle, its `\\.\DISPLAYn` name, and its two rectangles in px.</summary>
    internal sealed record DisplayMonitor(IntPtr Handle, string Device, RECT Bounds, RECT Work, bool Primary)
    {
        internal int Width => Bounds.Right - Bounds.Left;
        internal int Height => Bounds.Bottom - Bounds.Top;
        /// <summary>The number Windows' display settings shows: `\\.\DISPLAY2` is 2.</summary>
        internal int Number
        {
            get
            {
                int i = Device.Length;
                while (i > 0 && char.IsDigit(Device[i - 1])) i--;
                return int.TryParse(Device[i..], out int n) ? n : 0;
            }
        }
    }

    /// <summary>Every attached display, in the order Windows enumerates them. Never null.</summary>
    internal static List<DisplayMonitor> Displays()
    {
        var list = new List<DisplayMonitor>();
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT r, IntPtr data) =>
            {
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfoEx(h, ref mi))
                    list.Add(new DisplayMonitor(h, mi.szDevice ?? "", mi.rcMonitor, mi.rcWork, (mi.dwFlags & 1) != 0));
                return true;
            }, IntPtr.Zero);
        }
        catch { /* no displays: the caller falls back to a plausible rectangle */ }
        return list;
    }

    /// <summary>The display containing (or nearest to) a point — the `cursor` screen setting.</summary>
    internal static IntPtr DisplayAt(POINT p)
    {
        try { return MonitorFromPointW(p, 2 /* MONITOR_DEFAULTTONEAREST */); }
        catch { return IntPtr.Zero; }
    }

    /// <summary>A monitor's effective DPI, or 0 when it cannot be read (pre-8.1, or shcore gone).</summary>
    internal static uint DisplayDpi(IntPtr monitor)
    {
        try
        {
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out uint x, out _) == 0 && x > 0)
                return x;
        }
        catch { /* shcore.dll is absent below Windows 8.1 */ }
        return 0;
    }

    /// <summary>The system DPI — what a system-aware process (and every WPF default) would use.</summary>
    internal static uint SystemDpi()
    {
        try { var d = GetDpiForSystem(); return d > 0 ? d : 96; }
        catch { return 96; }
    }
}
