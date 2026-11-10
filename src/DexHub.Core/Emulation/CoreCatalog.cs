using DexHub.Core.Models;
using DexHub.Core.Settings;

namespace DexHub.Core.Emulation;

/// <summary>
/// Welcher eingebaute Core für welche Konsole zuständig ist.
/// Die Cores liegen im Ordner "cores" neben dem Programm (siehe scripts/FetchCores.cs).
/// </summary>
public static class CoreCatalog
{
    /// <summary>Konsolen, die bereits direkt im Programm laufen.</summary>
    public static readonly Dictionary<Platform, string> Cores = new()
    {
        [Platform.PokemonMini]    = "pokemini_libretro",
        [Platform.GameBoy]        = "gambatte_libretro",
        [Platform.GameBoyColor]   = "gambatte_libretro",
        [Platform.GameBoyAdvance] = "mgba_libretro",
        [Platform.NintendoDS]     = "melondsds_libretro",
        // 3D-Konsolen: brauchen OpenGL (siehe HwRender.cs), nur Windows/Linux
        [Platform.Nintendo64]     = "mupen64plus_next_libretro",
        [Platform.GameCube]       = "dolphin_libretro",
        [Platform.Wii]            = "dolphin_libretro",
        [Platform.Nintendo3DS]    = "citra_libretro",
    };

    public static string Extension =>
        OperatingSystem.IsWindows() ? ".dll" :
        OperatingSystem.IsMacOS() ? ".dylib" : ".so";

    /// <summary>Gesucht wird neben dem Programm und im Benutzerordner (für eigene Cores).</summary>
    public static IEnumerable<string> SearchFolders =>
    [
        Path.Combine(AppContext.BaseDirectory, "cores"),
        Path.Combine(AppSettings.AppDataFolder, "cores"),
    ];

    public static bool IsPlatformSupported(Platform platform) => Cores.ContainsKey(platform);

    /// <summary>
    /// Systemdateien, die manche Cores brauchen (Dolphin: Schriften und Datenbanken), beim ersten Start
    /// aus dem mitgelieferten ZIP in den System-Ordner entpacken.
    /// </summary>
    public static void EnsureSystemFiles(string coreName)
    {
        if (!coreName.StartsWith("dolphin", StringComparison.Ordinal)) return;
        var target = Path.Combine(AppSettings.SystemFolder, "dolphin-emu", "Sys");
        if (Directory.Exists(target)) return;
        foreach (var folder in SearchFolders)
        {
            var zip = Path.Combine(folder, "system", "Dolphin.zip");
            if (!File.Exists(zip)) continue;
            try
            {
                System.IO.Compression.ZipFile.ExtractToDirectory(zip, AppSettings.SystemFolder, overwriteFiles: true);
                Console.WriteLine("[DexHub] Dolphin-Systemdateien entpackt");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                Console.WriteLine("[DexHub] Dolphin-Systemdateien: " + ex.Message);
            }
            return;
        }
    }

    /// <summary>Android: Ordner, in dem die App ihre nativen Bibliotheken (die Cores) entpackt.</summary>
    public static string? AndroidLibraryFolder { get; set; }

    /// <summary>Ersatz, falls ein Core für Android nicht verfügbar ist (melonDS DS → klassisches melonDS).</summary>
    private static readonly Dictionary<string, string> AndroidFallbacks = new()
    {
        ["melondsds_libretro"] = "melonds_libretro",
    };

    public static string? FindCore(Platform platform)
    {
        if (!Cores.TryGetValue(platform, out var name)) return null;

        if (OperatingSystem.IsAndroid())
        {
            // In der APK heißen die Cores lib<name>.so
            foreach (var candidate in new[] { name, AndroidFallbacks.GetValueOrDefault(name) })
            {
                if (candidate is null) continue;
                var file = "lib" + candidate + ".so";
                if (AndroidLibraryFolder is { } dir && File.Exists(Path.Combine(dir, file))) return Path.Combine(dir, file);
            }
            return null;
        }

        foreach (var folder in SearchFolders)
        {
            var path = Path.Combine(folder, name + Extension);
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
