using ScreenCapture.NET;

namespace AKTelaCapture;

internal sealed class DisplayOption
{
    public DisplayOption(Display display, int number)
    {
        Display = display;
        Label = $"Tela {number}  •  {display.Width} × {display.Height}";
    }

    public Display Display { get; }
    public string Label { get; }

    public override string ToString() => Label;
}
