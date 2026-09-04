using ScreenCapture.NET;

namespace AKTelaCapture;

internal sealed class DisplayOption
{
    public DisplayOption(Display display, int number, int ffmpegOutputIndex)
    {
        Display = display;
        Number = number;
        FfmpegOutputIndex = ffmpegOutputIndex;
        Label = $"Tela {number}  •  {display.Width} × {display.Height}";
    }

    public Display Display { get; }
    public int Number { get; }
    public int FfmpegOutputIndex { get; }
    public string Label { get; }

    public override string ToString() => Label;
}
