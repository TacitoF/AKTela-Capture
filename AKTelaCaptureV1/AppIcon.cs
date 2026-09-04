namespace AKTelaCapture;

internal static class AppIcon
{
    public static Icon Load()
    {
        try
        {
            var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("AKTela.AppIcon.ico");
            if (stream is not null) return new Icon(stream);
        }
        catch { }
        return SystemIcons.Application;
    }
}
