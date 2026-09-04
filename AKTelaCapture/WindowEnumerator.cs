using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AKTelaCapture;

internal static class WindowEnumerator
{
    private const int DWMWA_CLOAKED = 14;

    public static IReadOnlyList<CaptureSourceOption> GetWindows()
    {
        var list = new List<CaptureSourceOption>();
        var own = Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var length = GetWindowTextLength(hwnd);
            if (length <= 0) return true;
            var title = new StringBuilder(length + 1);
            GetWindowText(hwnd, title, title.Capacity);
            var text = title.ToString().Trim();
            if (string.IsNullOrWhiteSpace(text)) return true;

            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || pid == own) return true;

            if (!TryGetClientScreenBounds(hwnd, out var bounds) || bounds.Width < 240 || bounds.Height < 160)
                return true;

            string processName;
            try { processName = Process.GetProcessById((int)pid).ProcessName; }
            catch { return true; }

            if (processName.Equals("explorer", StringComparison.OrdinalIgnoreCase) &&
                (text.Equals("Program Manager", StringComparison.OrdinalIgnoreCase) || text.Length < 2))
                return true;

            var shortTitle = text.Length > 46 ? text[..43] + "..." : text;
            list.Add(new CaptureSourceOption
            {
                Kind = CaptureSourceKind.Window,
                Label = $"{processName} · {shortTitle}",
                Width = bounds.Width,
                Height = bounds.Height,
                ScreenBounds = bounds,
                WindowHandle = hwnd,
                ProcessId = (int)pid,
                ProcessName = processName,
            });
            return true;
        }, IntPtr.Zero);

        return list
            .GroupBy(x => x.WindowHandle)
            .Select(g => g.First())
            .OrderBy(x => x.ProcessName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static bool TryGetClientScreenBounds(IntPtr hwnd, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (hwnd == IntPtr.Zero || !GetClientRect(hwnd, out var rect)) return false;
        var pt = new POINT { X = rect.Left, Y = rect.Top };
        if (!ClientToScreen(hwnd, ref pt)) return false;
        var width = Math.Max(0, rect.Right - rect.Left);
        var height = Math.Max(0, rect.Bottom - rect.Top);
        bounds = new Rectangle(pt.X, pt.Y, width, height);
        return width > 0 && height > 0;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
}
