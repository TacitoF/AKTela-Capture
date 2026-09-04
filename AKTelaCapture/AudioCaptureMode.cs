namespace AKTelaCapture;

internal enum AudioCaptureMode
{
    SourceOnly,
    SystemWithoutDiscord,
    SystemAll,
    Off
}

internal sealed record AudioOption(AudioCaptureMode Mode, string Label)
{
    public override string ToString() => Label;
}
