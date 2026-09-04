using ScreenCapture.NET;

namespace AKTelaCapture;

internal enum CaptureSourceKind
{
    Display,
    Window
}

internal sealed class CaptureSourceOption
{
    public CaptureSourceKind Kind { get; init; }
    public string Label { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public Rectangle ScreenBounds { get; init; }
    public Rectangle CaptureDisplayBounds { get; init; }
    public int FfmpegOutputIndex { get; init; }
    public Display? Display { get; init; }
    public IntPtr WindowHandle { get; init; }
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;

    public override string ToString() => Label;
}
