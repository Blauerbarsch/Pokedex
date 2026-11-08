using System.Text.Json;
using System.Text.Json.Serialization;
using DexHub.Core.Models;

namespace DexHub.Core.Settings;

/// <summary>Einstellungen, gespeichert als JSON im Benutzerordner.</summary>
public sealed class AppSettings
{
    /// <summary>Ordner mit den eigenen ROMs.</summary>
    public string RomFolder { get; set; } = "";

    /// <summary>Pfad zu retroarch.exe (Windows) bzw. RetroArch.app (Mac).</summary>
    public string RetroArchPath { get; set; } = DefaultRetroArchPath();

    /// <summary>Ordner mit den libretro-Cores.</summary>
    public string CoresFolder { get; set; } = DefaultCoresFolder();

    /// <summary>Ordner, in dem RetroArch Spielstände (.srm) ablegt.</summary>
    public string SavesFolder { get; set; } = DefaultSavesFolder();

    /// <summary>Optional: anderen Core pro Konsole erzwingen, z. B. "sameboy_libretro".</summary>
    public Dictionary<Platform, string> CoreOverrides { get; set; } = new();

    // ---------- Laden & Speichern ----------

    public static string AppDataFolder
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DexHub");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string SettingsFile => Path.Combine(AppDataFolder, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

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

    // ---------- Standardpfade ----------

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string DefaultRetroArchPath()
    {
        if (OperatingSystem.IsWindows()) return @"C:\RetroArch-Win64\retroarch.exe";
        if (OperatingSystem.IsMacOS()) return "/Applications/RetroArch.app";
        return "retroarch";
    }

    private static string DefaultCoresFolder()
    {
        if (OperatingSystem.IsWindows()) return @"C:\RetroArch-Win64\cores";
        if (OperatingSystem.IsMacOS()) return Path.Combine(Home, "Library/Application Support/RetroArch/cores");
        return Path.Combine(Home, ".config/retroarch/cores");
    }

    private static string DefaultSavesFolder()
    {
        if (OperatingSystem.IsWindows()) return @"C:\RetroArch-Win64\saves";
        if (OperatingSystem.IsMacOS()) return Path.Combine(Home, "Documents/RetroArch/saves");
        return Path.Combine(Home, ".config/retroarch/saves");
    }
}
