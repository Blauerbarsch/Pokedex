using System.Text.Json;

namespace DexHub.Core.Settings;

/// <summary>Einstellungen, gespeichert als JSON im Benutzerordner.</summary>
public sealed class AppSettings
{
    /// <summary>Ordner mit den eigenen ROMs.</summary>
    public string RomFolder { get; set; } = "";

    // ---------- Feste Ordner im Benutzerverzeichnis ----------

    /// <summary>Windows: %APPDATA%\DexHub, Mac: ~/Library/Application Support/DexHub</summary>
    public static string AppDataFolder => Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DexHub"));

    /// <summary>Spielstände (.srm), wie sie das Spiel selbst speichert.</summary>
    public static string SavesFolder => Ensure(Path.Combine(AppDataFolder, "saves"));

    /// <summary>Schnellspeicherungen (F5/F9).</summary>
    public static string StatesFolder => Ensure(Path.Combine(AppDataFolder, "states"));

    /// <summary>Optionale BIOS-/Firmware-Dateien für Cores, die so etwas wollen.</summary>
    public static string SystemFolder => Ensure(Path.Combine(AppDataFolder, "system"));

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---------- Laden & Speichern ----------

    private static string SettingsFile => Path.Combine(AppDataFolder, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile), JsonOptions)
                       ?? new AppSettings();
        }
        catch (JsonException)
        {
            // Kaputte Datei -> mit Standardwerten weitermachen
        }
        return new AppSettings();
    }

    public void Save() => File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, JsonOptions));
}
