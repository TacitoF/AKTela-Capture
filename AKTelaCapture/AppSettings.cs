using System.Text.Json;

namespace AKTelaCapture;

internal sealed class AppSettings
{
    public string RoomCode { get; set; } = string.Empty;
    public string Preset { get; set; } = "Jogo";
    public string Quality { get; set; } = "1080p60";
    public string SourceType { get; set; } = "Janela";
    public string CursorPolicy { get; set; } = "Auto";
    public string AudioMode { get; set; } = "Fonte";
    public bool MinimizeAfterStart { get; set; } = true;

    private static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AKTelaCapture");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this));
        }
        catch
        {
        }
    }
}
