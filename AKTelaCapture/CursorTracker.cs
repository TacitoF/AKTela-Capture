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
                var nx = 0d; var ny = 0d;

                if (show && source is not null && bounds.Width > 0 && bounds.Height > 0 &&
                    GetCursorInfo(out var info) && (info.flags & 0x00000001) != 0 && GetCursorPos(out var point))
                {
                    visible = bounds.Contains(point.X, point.Y);
                    if (visible)
                    {
                        nx = Math.Clamp((point.X - bounds.Left) / (double)bounds.Width, 0, 1);
                        ny = Math.Clamp((point.Y - bounds.Top) / (double)bounds.Height, 0, 1);
                    }
                }

                if (visible != _lastVisible || Math.Abs(nx - _lastX) > 0.0015 || Math.Abs(ny - _lastY) > 0.0015)
                {
                    _lastVisible = visible; _lastX = nx; _lastY = ny;
                    _relay.TryQueueControl(new { type = "cursor", x = nx, y = ny, visible });
                }
            }
            catch { }

            try { await Task.Delay(33, token); } catch { break; }
        }
    }

    private static Rectangle GetBounds(CaptureSourceOption source)
    {
        if (source.Kind == CaptureSourceKind.Window && WindowEnumerator.TryGetClientScreenBounds(source.WindowHandle, out var current))
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

    private static bool GetCursorInfo(out CURSORINFO info)
    {
        info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        return NativeGetCursorInfo(ref info);
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll", EntryPoint = "GetCursorInfo")]
    private static extern bool NativeGetCursorInfo(ref CURSORINFO pci);
}
