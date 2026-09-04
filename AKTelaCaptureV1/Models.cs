namespace AKTelaCapture;

internal enum SourceKind { Display, Window }
internal enum AudioMode { SourceOnly, SystemWithoutDiscord, Off }

internal sealed record QualityOption(string Key, string Label, int Width, int Height, int Fps, int BitrateMbps)
{
    public override string ToString() => Label;
    public static readonly QualityOption[] All =
    [
        new("720p30", "720p · 30 FPS", 1280, 720, 30, 4),
        new("720p60", "720p · 60 FPS", 1280, 720, 60, 6),
        new("1080p30", "1080p · 30 FPS", 1920, 1080, 30, 8),
        new("1080p60", "1080p · 60 FPS", 1920, 1080, 60, 12)
    ];
}

internal sealed record CaptureSource(
    SourceKind Kind,
    string Label,
    Rectangle Bounds,
    int OutputIndex,
    IntPtr WindowHandle,
    int ProcessId,
    string ProcessName)
{
    public int Width => Math.Max(2, Bounds.Width);
    public int Height => Math.Max(2, Bounds.Height);
    public override string ToString() => Label;
}

internal sealed record StreamConfig(
    int Width,
    int Height,
    int Fps,
    int BitrateMbps,
    bool AudioEnabled,
    string Preset,
    string CursorPolicy);
