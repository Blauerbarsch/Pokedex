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

    /// <summary>Team-Planer: pro Benutzer bis zu 6 Pokémon (Art + PID eines eigenen Pokémon, 0 = nur die Art).</summary>
    public Dictionary<string, List<TeamMember>> Teams { get; set; } = [];

    /// <summary>Beim Schließen automatisch sichern und beim nächsten Start dort weitermachen.</summary>
    public bool AutoResume { get; set; } = true;

    /// <summary>Ordner für automatische Sicherungen (leer = aus).</summary>
    public string AutoBackupFolder { get; set; } = "";

    /// <summary>Wie oft gesichert wird (Stunden; 0 = bei jedem Programmstart).</summary>
    public int AutoBackupHours { get; set; } = 24;

    /// <summary>Wie viele Sicherungen aufgehoben werden.</summary>
    public int AutoBackupKeep { get; set; } = 10;

    public DateTime? LastAutoBackup { get; set; }

    // ---------- Feste Ordner im Benutzerverzeichnis ----------

    /// <summary>Windows: %APPDATA%\DexHub, Mac: ~/Library/Application Support/DexHub</summary>
    public static string AppDataFolder => Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DexHub"));

    // ---------- Benutzer ----------

    public const string DefaultProfile = "Standard";

    /// <summary>Alle angelegten Benutzer.</summary>
    public List<string> Profiles { get; set; } = [DefaultProfile];

    /// <summary>Wer zuletzt gespielt hat.</summary>
    public string CurrentProfile { get; set; } = DefaultProfile;

    /// <summary>
    /// Benutzer, dessen Spielstände gerade benutzt werden. Statisch, weil Emulator, Spielstand-Suche usw.
    /// darauf zugreifen. Wird beim Start und beim Wechseln gesetzt.
    /// </summary>
    public static string ActiveProfile { get; set; } = DefaultProfile;

    /// <summary>Ordner eines Benutzers. "Standard" nutzt die ursprünglichen Ordner (kompatibel zu älteren Versionen).</summary>
    private static string ProfileFolder(string profile, string sub) =>
        profile == DefaultProfile
            ? Ensure(Path.Combine(AppDataFolder, sub))
            : Ensure(Path.Combine(AppDataFolder, "profiles", SafeName(profile), sub));

    /// <summary>Spielstände (.srm) eines Benutzers.</summary>
    public static string SavesFolderFor(string profile) => ProfileFolder(profile, "saves");

    /// <summary>Eine Datei direkt im Ordner eines Benutzers (beim Standard-Benutzer im DexHub-Ordner).</summary>
    public static string ProfileFile(string profile, string name) =>
        profile == DefaultProfile
            ? Path.Combine(AppDataFolder, name)
            : Path.Combine(Ensure(Path.Combine(AppDataFolder, "profiles", SafeName(profile))), name);

    /// <summary>Pokémon-Bank eines Benutzers.</summary>
    public static string BankFolderFor(string profile) => ProfileFolder(profile, "bank");

    /// <summary>Schnellspeicherungen eines Benutzers.</summary>
    public static string StatesFolderFor(string profile) => ProfileFolder(profile, "states");

    /// <summary>Spielstände (.srm) des aktiven Benutzers.</summary>
    public static string SavesFolder => SavesFolderFor(ActiveProfile);

    /// <summary>Schnellspeicherungen (F5/F9) des aktiven Benutzers.</summary>
    public static string StatesFolder => StatesFolderFor(ActiveProfile);

    /// <summary>Sicherungen vor einem Tausch.</summary>
    public static string BackupsFolder => Ensure(Path.Combine(AppDataFolder, "backups"));

    /// <summary>Entfernt Zeichen, die in Ordnernamen nicht erlaubt sind.</summary>
    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "Benutzer" : cleaned;
    }

    /// <summary>Optionale BIOS-/Firmware-Dateien für Cores, die so etwas wollen.</summary>
    public static string SystemFolder => Ensure(Path.Combine(AppDataFolder, "system"));

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---------- Laden & Speichern ----------

    private static string SettingsFile => Path.Combine(AppDataFolder, "settings.json");

    /// <summary>Pfad der Einstellungsdatei (Spiel-Prozesse beobachten sie auf Änderungen).</summary>
    public static string SettingsFilePath => SettingsFile;

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
        if (settings.Profiles.Count == 0) settings.Profiles.Add(DefaultProfile);
        if (!settings.Profiles.Contains(settings.CurrentProfile)) settings.CurrentProfile = settings.Profiles[0];
        return settings;
    }

    public void Save() => File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, JsonOptions));
}

/// <summary>Ein Platz im Team-Planer.</summary>
public sealed class TeamMember
{
    public int Species { get; set; }
    public uint Pid { get; set; }
}
