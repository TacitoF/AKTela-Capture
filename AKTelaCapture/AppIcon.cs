using System.Reflection;

namespace AKTelaCapture;

internal static class AppIcon
{
    public static Icon Load()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("AKTela.AppIcon.ico");
            if (stream is not null)
                return new Icon(stream);
        }
        catch
        {
        }

        return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
    }
}
