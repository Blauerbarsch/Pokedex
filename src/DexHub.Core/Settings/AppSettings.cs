using System.Text.Json;
using System.Text.Json.Serialization;
using DexHub.Core.Models;

namespace DexHub.Core.Settings;

public enum FastForwardMode
{
    /// <summary>Läuft schnell, solange die Taste gedrückt ist.</summary>
    Hold,
    /// <summary>Ein Druck schaltet ein, der nächste wieder aus.</summary>
    Toggle,
}

/// <summary>Einstellungen, gespeichert als JSON im Benutzerordner.</summary>
public sealed class AppSettings
{
    /// <summary>Ordner mit den eigenen ROMs.</summary>
    public string RomFolder { get; set; } = "";

    /// <summary>Tastatur- und Controller-Belegung.</summary>
    public InputSettings Input { get; set; } = new();

    /// <summary>Lautstärke 0–100 (wird gehörrichtig umgerechnet, siehe VolumeGain).</summary>
    public int Volume { get; set; } = 50;

    /// <summary>
    /// Das Ohr empfindet Lautstärke nicht linear. Mit dem Quadrat fühlt sich der Regler gleichmäßig an:
    /// 50 % auf dem Regler = 25 % Signal.
    /// </summary>
    public float VolumeGain => (Volume / 100f) * (Volume / 100f);

    /// <summary>Schnellvorlauf: gedrückt halten oder per Tastendruck an/aus schalten.</summary>
    public FastForwardMode FastForwardMode { get; set; } = FastForwardMode.Hold;

    /// <summary>Wie schnell der Schnellvorlauf läuft (2 = doppelt so schnell). 0 = so schnell wie möglich.</summary>
    public int FastForwardSpeed { get; set; } = 3;

    /// <summary>Bild-Einstellungen pro Konsole.</summary>
    public Dictionary<Platform, VideoSettings> Video { get; set; } = [];

    public VideoSettings VideoFor(Platform platform)
    {
        if (!Video.TryGetValue(platform, out var video))
            Video[platform] = video = new VideoSettings();
        return video;
    }

    /// <summary>Eigene Werte für Emulator-Optionen: Core-Name -> (Option -> Wert).</summary>
    public Dictionary<string, Dictionary<string, string>> CoreOptions { get; set; } = [];

    /// <summary>Eingeklappte Konsolen in der Bibliothek.</summary>
    public HashSet<Platform> CollapsedPlatforms { get; set; } = [];

    /// <summary>Aufgeklappte Generationen im Pokédex (Standard: nur Gen 1).</summary>
    public HashSet<int> ExpandedGenerations { get; set; } = [1];

    /// <summary>Beim Schließen automatisch sichern und beim nächsten Start dort weitermachen.</summary>
    public bool AutoResume { get; set; } = true;

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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }, // Enums als Text, damit die Datei lesbar bleibt
    };

    public static AppSettings Load()
    {
        var settings = new AppSettings();
        try
        {
            if (File.Exists(SettingsFile))
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile), JsonOptions)
                           ?? new AppSettings();
        }
        catch (JsonException)
        {
            // Kaputte Datei -> mit Standardwerten weitermachen
        }
        settings.Input ??= new InputSettings();
        settings.Input.EnsureDefaults();
        return settings;
    }

    public void Save() => File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, JsonOptions));
}
