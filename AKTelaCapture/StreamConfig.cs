namespace AKTelaCapture;

internal sealed record StreamConfig(
    int Width,
    int Height,
    int Fps,
    int VideoBitrateMbps,
    bool AudioEnabled,
    string PresetName,
    string SourceKind,
    string AudioMode,
    string CursorPolicy)
{
    public string ResolutionLabel => Height >= 1080 ? "1080p" : "720p";
}

internal sealed record QualityOption(string Key, string Label, int Width, int Height, int Fps, int BitrateMbps)
{
    public override string ToString() => Label;

    public static readonly QualityOption[] All =
    [
        new("720p30", "720p · 30 FPS · leve", 1280, 720, 30, 4),
        new("720p60", "720p · 60 FPS · fluido", 1280, 720, 60, 6),
        new("1080p30", "1080p · 30 FPS · nítido", 1920, 1080, 30, 8),
        new("1080p60", "1080p · 60 FPS · jogos", 1920, 1080, 60, 12),
    ];
}
