namespace AKTelaCapture;

internal sealed record StreamConfig(int Fps, int VideoBitrateMbps, bool AudioEnabled)
{
    public int Width => 1920;
    public int Height => 1080;
}
