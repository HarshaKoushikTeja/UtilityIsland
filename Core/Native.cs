using System.Runtime.InteropServices;
using System.Text;

namespace DynamicIsland.Core;

/// <summary>Win32 calls the island needs.</summary>
internal static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080L;
    public const long WS_EX_NOACTIVATE = 0x08000000L;
    public const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int SW_RESTORE = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    /// <summary>
    /// One display, in screen pixels. Name is the Windows device name (\\.\DISPLAY1), which
    /// stays the same when a game changes resolution; the handle does not.
    /// </summary>
    public sealed class Monitor
    {
        public IntPtr Handle;
        public string Name = "";
        public int Left, Top, Right, Bottom;
        public bool Primary;
        public double Dpi = 96;

        public int Width => Right - Left;
        public int Height => Bottom - Top;

        /// <summary>"Display 2 · 2560 × 1440 · main" for the control panel.</summary>
        public string Label
        {
            get
            {
                string digits = new string(Name.Where(char.IsDigit).ToArray());
                string label = "Display " + (digits.Length > 0 ? digits : "?") + " · " + Width + " × " + Height;
                return Primary ? label + " · main" : label;
            }
        }
    }

    public delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);

    [DllImport("user32.dll")]
    public static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback,
        uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    /// <summary>Keeps a window out of screenshots, recordings and screen sharing (Windows 10 2004+).</summary>
    public static void SetHiddenFromCapture(IntPtr hwnd, bool hidden)
    {
        const uint WDA_NONE = 0x0, WDA_EXCLUDEFROMCAPTURE = 0x11;
        SetWindowDisplayAffinity(hwnd, hidden ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
    }

    [DllImport("kernel32.dll")]
    public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    [DllImport("kernel32.dll")]
    public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    /// <summary>Keep the island out of Alt-Tab and stop it stealing focus when clicked.</summary>
    public static void MakeOverlay(IntPtr hwnd)
    {
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        style |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
    }

    private static Monitor? Describe(IntPtr handle)
    {
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (handle == IntPtr.Zero || !GetMonitorInfo(handle, ref info)) return null;

        double dpi = 96;
        try
        {
            if (GetDpiForMonitor(handle, 0, out uint dpiX, out _) == 0 && dpiX > 0) dpi = dpiX;
        }
        catch
        {
            // shcore missing on very old Windows: assume 100% scaling
        }

        return new Monitor
        {
            Handle = handle,
            Name = info.szDevice ?? "",
            Left = info.rcMonitor.Left,
            Top = info.rcMonitor.Top,
            Right = info.rcMonitor.Right,
            Bottom = info.rcMonitor.Bottom,
            Primary = (info.dwFlags & 1) != 0,
            Dpi = dpi
        };
    }

    /// <summary>All connected displays.</summary>
    public static List<Monitor> GetMonitors()
    {
        var list = new List<Monitor>();
        MonitorEnumProc callback = (handle, _, _, _) =>
        {
            Monitor? monitor = Describe(handle);
            if (monitor != null) list.Add(monitor);
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return list;
    }

    /// <summary>The display containing a screen point (nearest one if it's between displays).</summary>
    public static Monitor? MonitorAt(double x, double y)
    {
        return Describe(MonitorFromPoint(new POINT { X = (int)Math.Round(x), Y = (int)Math.Round(y) }, 2));
    }

    public static IntPtr ForegroundWindow() => GetForegroundWindow();

    /// <summary>
    /// True when the window is alive, visible and covers every pixel of the display it is on
    /// (a game, or a video in fullscreen). Reports that display's device name.
    /// </summary>
    public static bool IsFullscreen(IntPtr hwnd, out string monitorName)
    {
        monitorName = "";
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
        // A maximized window can be screen-sized (auto-hide taskbar) without being fullscreen.
        if (IsZoomed(hwnd)) return false;

        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        string cls = sb.ToString();
        // The desktop and taskbar are screen-sized but are not fullscreen apps.
        if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return false;

        if (!GetWindowRect(hwnd, out RECT r)) return false;

        Monitor? m = Describe(MonitorFromWindow(hwnd, 2)); // nearest display
        if (m == null) return false;

        if (r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom)
        {
            monitorName = m.Name;
            return true;
        }
        return false;
    }

    /// <summary>Top-left corner of a window, in screen pixels.</summary>
    public static (int X, int Y) WindowPosition(IntPtr hwnd)
    {
        return GetWindowRect(hwnd, out RECT r) ? (r.Left, r.Top) : (0, 0);
    }

    /// <summary>Moves a window to a screen position in pixels without resizing or activating it.</summary>
    public static void MoveWindow(IntPtr hwnd, int x, int y)
    {
        const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Puts the window back at the top of the always-on-top band. Some apps (WhatsApp while it
    /// starts, for one) shuffle the z-order and leave other always-on-top windows underneath.
    /// </summary>
    public static void KeepOnTop(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200;
        SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    private static WinEventProc? _foregroundProc; // kept alive for as long as the hook exists
    private static IntPtr _foregroundHook;

    /// <summary>Calls back (on the UI thread) every time a different window comes to the front.</summary>
    public static void WatchForeground(Action changed)
    {
        const uint EVENT_SYSTEM_FOREGROUND = 0x0003, WINEVENT_OUTOFCONTEXT = 0x0000;
        if (_foregroundHook != IntPtr.Zero) return;
        _foregroundProc = (_, _, _, _, _, _, _) => changed();
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _foregroundProc, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    public static void StopWatchingForeground()
    {
        if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
        _foregroundHook = IntPtr.Zero;
        _foregroundProc = null;
    }

    /// <summary>Seconds since the last keyboard or mouse input.</summary>
    public static double IdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        uint now = unchecked((uint)Environment.TickCount);
        return unchecked(now - info.dwTime) / 1000.0;
    }

    public static void BringToFront(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
    }
}
