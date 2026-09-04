using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AKTelaCapture;

internal static class SourceEnumerator
{
    public static IReadOnlyList<CaptureSource> Displays()
    {
        var screens = Screen.AllScreens;
        var list = new List<CaptureSource>();
        for (var i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            list.Add(new CaptureSource(SourceKind.Display, $"Tela {i + 1} · {screen.Bounds.Width} × {screen.Bounds.Height}", screen.Bounds, OutputIndex(screen, i), IntPtr.Zero, 0, string.Empty));
        }
        return list;
    }

    public static IReadOnlyList<CaptureSource> Windows()
    {
        var list = new List<CaptureSource>();
        var ownPid = Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindowTextLength(hwnd) <= 0) return true;
            var title = new StringBuilder(GetWindowTextLength(hwnd) + 1);
            GetWindowText(hwnd, title, title.Capacity);
            var text = title.ToString().Trim(); if (text.Length == 0) return true;
            GetWindowThreadProcessId(hwnd, out var pid); if (pid == 0 || pid == ownPid) return true;
            if (!TryGetBounds(hwnd, out var bounds) || bounds.Width < 240 || bounds.Height < 160) return true;
            string processName; try { processName = Process.GetProcessById((int)pid).ProcessName; } catch { return true; }
            var screen = Screen.FromRectangle(bounds);
            var fallback = Array.FindIndex(Screen.AllScreens, s => s.DeviceName == screen.DeviceName);
            if (fallback < 0) fallback = 0;
            var shortTitle = text.Length > 46 ? text[..43] + "..." : text;
            list.Add(new CaptureSource(SourceKind.Window, $"{processName} · {shortTitle}", bounds, OutputIndex(screen, fallback), hwnd, (int)pid, processName));
            return true;
        }, IntPtr.Zero);
        return list.OrderBy(x => x.ProcessName).ThenBy(x => x.Label).ToList();
    }

    public static bool TryGetBounds(IntPtr hwnd, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (!GetClientRect(hwnd, out var rc)) return false;
        var p = new POINT { X = rc.Left, Y = rc.Top };
        if (!ClientToScreen(hwnd, ref p)) return false;
        bounds = new Rectangle(p.X, p.Y, Math.Max(0, rc.Right - rc.Left), Math.Max(0, rc.Bottom - rc.Top));
        return bounds.Width > 0 && bounds.Height > 0;
    }

    private static int OutputIndex(Screen screen, int fallback)
    {
        var name = screen.DeviceName;
        var digits = new string(name.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return int.TryParse(digits, out var n) && n > 0 ? n - 1 : Math.Max(0, fallback);
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
}
