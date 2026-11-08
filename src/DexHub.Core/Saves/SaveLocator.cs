using DexHub.Core.Models;
using DexHub.Core.Settings;

namespace DexHub.Core.Saves;

/// <summary>Findet den Spielstand zu einer ROM (RetroArch speichert als .srm).</summary>
public static class SaveLocator
{
    private static readonly string[] SaveExtensions = [".srm", ".sav"];

    public static string? Find(GameEntry game, AppSettings settings)
    {
        var baseName = Path.GetFileNameWithoutExtension(game.RomPath);
        var folders = new List<string>();

        // 1. RetroArch-Spielstandordner (ggf. mit Unterordnern pro Core, z. B. "saves/Gambatte")
        if (Directory.Exists(settings.SavesFolder))
        {
            folders.Add(settings.SavesFolder);
            folders.AddRange(Directory.GetDirectories(settings.SavesFolder));
        }

        // 2. Neben der ROM
        if (Path.GetDirectoryName(game.RomPath) is { } romDir)
            folders.Add(romDir);

        foreach (var folder in folders)
            foreach (var ext in SaveExtensions)
            {
                var candidate = Path.Combine(folder, baseName + ext);
                if (File.Exists(candidate)) return candidate;
            }

        return null;
    }
}
