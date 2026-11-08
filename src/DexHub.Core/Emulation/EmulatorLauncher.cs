using System.Diagnostics;
using DexHub.Core.Models;
using DexHub.Core.Settings;

namespace DexHub.Core.Emulation;

/// <summary>
/// Startet Spiele über RetroArch mit dem passenden libretro-Core.
/// Später kann man die Cores auch direkt einbinden – für den Anfang ist RetroArch der einfachste Weg.
/// </summary>
public sealed class EmulatorLauncher(AppSettings settings)
{
    /// <summary>Bevorzugte Cores pro Konsole, der erste vorhandene wird genommen.</summary>
    private static readonly Dictionary<Platform, string[]> DefaultCores = new()
    {
        [Platform.PokemonMini]    = ["pokemini_libretro"],
        [Platform.GameBoy]        = ["gambatte_libretro", "sameboy_libretro", "mgba_libretro"],
        [Platform.GameBoyColor]   = ["gambatte_libretro", "sameboy_libretro", "mgba_libretro"],
        [Platform.GameBoyAdvance] = ["mgba_libretro", "vbam_libretro"],
        [Platform.Nintendo64]     = ["mupen64plus_next_libretro", "parallel_n64_libretro"],
        [Platform.NintendoDS]     = ["melondsds_libretro", "melonds_libretro", "desmume_libretro"],
        [Platform.GameCube]       = ["dolphin_libretro"],
        [Platform.Wii]            = ["dolphin_libretro"],
        [Platform.Nintendo3DS]    = ["citra_libretro"],
    };

    private static string CoreExtension =>
        OperatingSystem.IsWindows() ? ".dll" :
        OperatingSystem.IsMacOS() ? ".dylib" : ".so";

    /// <summary>Sucht den Core für eine Konsole. Gibt null zurück, wenn keiner installiert ist.</summary>
    public string? FindCore(Platform platform)
    {
        string[] candidates = settings.CoreOverrides.TryGetValue(platform, out var custom)
            ? [custom]
            : DefaultCores.GetValueOrDefault(platform, []);

        foreach (var name in candidates)
        {
            var path = Path.Combine(settings.CoresFolder, name + CoreExtension);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public static string CoreHint(Platform platform) =>
        string.Join(" oder ", DefaultCores.GetValueOrDefault(platform, []));

    /// <summary>Startet das Spiel. Wirft eine Exception mit verständlicher Meldung, wenn etwas fehlt.</summary>
    public Process Launch(GameEntry game)
    {
        var exe = ResolveRetroArchExecutable();
        var core = FindCore(game.Info.Platform)
            ?? throw new InvalidOperationException(
                $"Kein Core für {game.PlatformName} gefunden in:\n{settings.CoresFolder}\n\n" +
                $"In RetroArch unter „Online-Aktualisierung → Core-Downloader“ installieren: {CoreHint(game.Info.Platform)}");

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.ArgumentList.Add("-L");
        psi.ArgumentList.Add(core);
        psi.ArgumentList.Add(game.RomPath);

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("RetroArch konnte nicht gestartet werden.");
        process.EnableRaisingEvents = true;
        return process;
    }

    private string ResolveRetroArchExecutable()
    {
        var path = settings.RetroArchPath;

        // Auf dem Mac zeigt man meist auf die .app – die eigentliche Programmdatei liegt darin.
        if (path.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            path = Path.Combine(path, "Contents", "MacOS", "RetroArch");

        if (Path.IsPathRooted(path) && !File.Exists(path))
            throw new InvalidOperationException(
                $"RetroArch wurde nicht gefunden:\n{path}\n\nBitte in den Einstellungen den richtigen Pfad angeben.");

        return path;
    }
}
