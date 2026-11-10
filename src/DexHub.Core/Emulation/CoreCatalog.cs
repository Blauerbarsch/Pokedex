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
        // N64, GameCube, Wii, 3DS brauchen 3D-Hardware-Rendering -> kommt in einem späteren Schritt
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
