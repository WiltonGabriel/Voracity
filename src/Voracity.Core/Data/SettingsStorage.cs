using System.Text.Json;

namespace Voracity.Core.Data;

public sealed record GameSettings
{
    public string LanguageMode { get; init; } = "auto";
    public string WindowMode { get; init; } = "windowed";
    public bool Vsync { get; init; } = true;
    public float UiScale { get; init; } = 1.0f;
    public int DefaultSpeed { get; init; } = 2;
    public float MasterVolume { get; init; } = 0.8f;
    public bool DebugMode { get; init; } = false;

    public string ResolveActiveLanguage() => LocalizationCatalog.ResolveLanguageCode(LanguageMode);
}

/// <summary>
/// Offline user preferences &amp; settings store (Linux ~/.local/share/Voracity, Windows %APPDATA%\Voracity).
/// </summary>
public static class SettingsStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string GetDefaultUserDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
            appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(appData, "Voracity");
    }

    public static GameSettings Load(string? userDirectory = null)
    {
        var dir = userDirectory ?? GetDefaultUserDirectory();
        var path = Path.Combine(dir, "settings.json");
        if (!File.Exists(path))
            return new GameSettings();

        try
        {
            return JsonSerializer.Deserialize<GameSettings>(File.ReadAllText(path)) ?? new GameSettings();
        }
        catch
        {
            return new GameSettings();
        }
    }

    public static void Save(GameSettings settings, string? userDirectory = null)
    {
        var dir = userDirectory ?? GetDefaultUserDirectory();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
