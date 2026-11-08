using DexHub.Core.Models;
using DexHub.Core.Settings;

namespace DexHub.Core.Saves;

/// <summary>Findet den Spielstand zu einer ROM.</summary>
public static class SaveLocator
{
    public static string? Find(GameEntry game)
    {
        var baseName = Path.GetFileNameWithoutExtension(game.RomPath);

        // 1. Eigener Spielstand-Ordner von DexHub (manche Cores legen Unterordner an)
        var own = Path.Combine(AppSettings.SavesFolder, baseName + ".srm");
        if (File.Exists(own)) return own;

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var ext in new[] { ".srm", ".sav", ".dsv" })
        {
            var match = Directory.EnumerateFiles(AppSettings.SavesFolder, baseName + ext, options)
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .FirstOrDefault();
            if (match is not null) return match;
        }

        // 2. Neben der ROM (z. B. von anderen Emulatoren übernommen)
        if (Path.GetDirectoryName(game.RomPath) is { } romDir)
            foreach (var ext in new[] { ".srm", ".sav" })
            {
                var candidate = Path.Combine(romDir, baseName + ext);
                if (File.Exists(candidate)) return candidate;
            }

        return null;
    }
}
