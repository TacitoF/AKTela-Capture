using System.Runtime.InteropServices;

namespace AKTelaCapture;

internal sealed class CursorTracker : IAsyncDisposable
{
    private readonly RelayClient _relay;
    private CancellationTokenSource? _cts;
    private Task? _task;
    private CaptureSourceOption? _source;
    private Func<bool>? _shouldShow;
    private double _lastX = -1, _lastY = -1;
    private bool _lastVisible;
    private IntPtr _lastCursorHandle = IntPtr.Zero;
    private int _cursorWidth = 32, _cursorHeight = 32;
    private double _hotspotX = 0.05, _hotspotY = 0.05;

    public CursorTracker(RelayClient relay) => _relay = relay;

    public void Start(CaptureSourceOption source, Func<bool> shouldShow)
    {
        Stop();
        _source = source;
        _shouldShow = shouldShow;
        _cts = new CancellationTokenSource();
        _task = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;
        _task = null;
        _source = null;
        _lastCursorHandle = IntPtr.Zero;
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var source = _source;
                var show = _shouldShow?.Invoke() == true;
                var bounds = source is null ? Rectangle.Empty : GetBounds(source);
                var visible = false;
                var nx = 0d;
                var ny = 0d;
                var nw = 0d;
                var nh = 0d;
                var geometryChanged = false;

                if (show && source is not null && bounds.Width > 0 && bounds.Height > 0 &&
                    GetCursorInfo(out var info) && (info.flags & 0x00000001) != 0 &&
                    GetCursorPos(out var point))
                {
                    visible = bounds.Contains(point.X, point.Y);
                    if (visible)
                    {
                        if (info.hCursor != _lastCursorHandle)
                        {
                            UpdateCursorGeometry(info.hCursor);
                            geometryChanged = true;
                        }

                        nx = Math.Clamp((point.X - bounds.Left) / (double)bounds.Width, 0, 1);
                        ny = Math.Clamp((point.Y - bounds.Top) / (double)bounds.Height, 0, 1);
                        nw = Math.Clamp(_cursorWidth / (double)bounds.Width, 0.002, 0.08);
                        nh = Math.Clamp(_cursorHeight / (double)bounds.Height, 0.002, 0.08);
                    }
                }

                if (geometryChanged || visible != _lastVisible ||
                    Math.Abs(nx - _lastX) > 0.0015 ||
                    Math.Abs(ny - _lastY) > 0.0015)
                {
                    _lastVisible = visible;
                    _lastX = nx;
                    _lastY = ny;
                    _relay.TryQueueControl(new
                    {
                        type = "cursor",
                        x = nx,
                        y = ny,
                        visible,
                        w = nw,
                        h = nh,
                        hx = _hotspotX,
                        hy = _hotspotY
                    });
                }
            }
            catch { }

            try { await Task.Delay(33, token); }
            catch { break; }
        }
    }

    private void UpdateCursorGeometry(IntPtr hCursor)
    {
        _lastCursorHandle = hCursor;
        if (hCursor == IntPtr.Zero || !GetIconInfo(hCursor, out var iconInfo)) return;

        try
        {
            var width = 0;
            var height = 0;

            if (iconInfo.hbmColor != IntPtr.Zero &&
                GetObject(iconInfo.hbmColor, Marshal.SizeOf<BITMAP>(), out var colorBitmap) != 0)
            {
                width = Math.Abs(colorBitmap.bmWidth);
                height = Math.Abs(colorBitmap.bmHeight);
            }
            else if (iconInfo.hbmMask != IntPtr.Zero &&
                     GetObject(iconInfo.hbmMask, Marshal.SizeOf<BITMAP>(), out var maskBitmap) != 0)
            {
                width = Math.Abs(maskBitmap.bmWidth);
                // Cursores monocromáticos armazenam AND/XOR empilhados verticalmente.
                height = Math.Max(1, Math.Abs(maskBitmap.bmHeight) / 2);
            }

            if (width > 0 && height > 0)
            {
                _cursorWidth = width;
                _cursorHeight = height;
                _hotspotX = Math.Clamp(iconInfo.xHotspot / (double)width, 0, 1);
                _hotspotY = Math.Clamp(iconInfo.yHotspot / (double)height, 0, 1);
            }
        }
        finally
        {
            if (iconInfo.hbmColor != IntPtr.Zero) DeleteObject(iconInfo.hbmColor);
            if (iconInfo.hbmMask != IntPtr.Zero) DeleteObject(iconInfo.hbmMask);
        }
    }

    private static Rectangle GetBounds(CaptureSourceOption source)
    {
        if (source.Kind == CaptureSourceKind.Window &&
            WindowEnumerator.TryGetClientScreenBounds(source.WindowHandle, out var current))
            return current;
        return source.ScreenBounds;
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    private static bool GetCursorInfo(out CURSORINFO info)
    {
        info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        return NativeGetCursorInfo(ref info);
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", EntryPoint = "GetCursorInfo")]
    private static extern bool NativeGetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("gdi32.dll", CharSet = CharSet.Auto)]
    private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, out BITMAP lpvObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
