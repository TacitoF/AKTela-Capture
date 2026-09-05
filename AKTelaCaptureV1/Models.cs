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

    public static QualityOption ByKey(string? key) => All.FirstOrDefault(q => q.Key == key) ?? All[0];

    public static int Rank(string? key) => key switch
    {
        "720p30" => 0,
        "720p60" => 1,
        "1080p30" => 2,
        "1080p60" => 3,
        _ => 0
    };

    public static string LowerOneStep(string key) => key switch
    {
        "1080p60" => "1080p30",
        "1080p30" => "720p30",
        "720p60" => "720p30",
        _ => "720p30"
    };

    public static string HigherOneStep(string key, string ceiling) => key switch
    {
        "720p30" when Rank(ceiling) >= Rank("1080p30") => "1080p30",
        "720p30" when Rank(ceiling) >= Rank("720p60") => "720p60",
        "720p60" when Rank(ceiling) >= Rank("1080p30") => "1080p30",
        "1080p30" when Rank(ceiling) >= Rank("1080p60") => "1080p60",
        _ => key
    };

    public static string Min(string a, string b) => Rank(a) <= Rank(b) ? a : b;
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
    string QualityKey,
    int Width,
    int Height,
    int Fps,
    int BitrateMbps,
    bool AudioEnabled,
    string Preset,
    string CursorPolicy,
    string VideoCodec,
    string VideoProfile,
    string ExpectedCodec,
    bool CompatibilityMode)
{
    public string ModeLabel => $"{Height}p · {Fps} FPS";
}

internal sealed record AudienceCapabilities(
    int Viewers,
    int ReadyViewers,
    bool Ready,
    string ModeKey,
    string VideoCodec,
    string VideoProfile,
    string CodecString,
    bool CompatibilityMode,
    string Reason)
{
    public static AudienceCapabilities Default(int viewers = 0) => new(
        viewers,
        0,
        viewers == 0,
        "720p30",
        "h264",
        "baseline",
        "avc1.42E01F",
        true,
        viewers == 0 ? "sem espectadores" : "aguardando recursos dos espectadores");
}

internal sealed record VideoDiagnostics(
    string Encoder,
    string Codec,
    string Profile,
    string CodecString,
    double Fps,
    long Frames,
    long Keyframes,
    long Restarts);

internal sealed record RelayDiagnostics(
    bool Connected,
    int Viewers,
    long LatencyMs,
    long VideoSent,
    long AudioSent,
    long VideoDropped,
    long AudioDropped,
    int VideoQueue,
    int AudioQueue,
    int Reconnects,
    string LastError);
