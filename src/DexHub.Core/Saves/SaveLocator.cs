using DexHub.Core.Models;
using DexHub.Core.Settings;

namespace DexHub.Core.Saves;

/// <summary>Findet den Spielstand zu einer ROM.</summary>
public static class SaveLocator
{
    /// <summary>Spielstand des aktiven Benutzers.</summary>
    public static string? Find(GameEntry game) => Find(game, AppSettings.ActiveProfile);

    public static string? Find(GameEntry game, string profile)
    {
        var baseName = Path.GetFileNameWithoutExtension(game.RomPath);
        var savesFolder = AppSettings.SavesFolderFor(profile);

        // 1. Eigener Spielstand-Ordner des Benutzers (manche Cores legen Unterordner an)
        var own = OwnSavePath(game, profile);
        if (File.Exists(own)) return own;

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var ext in new[] { ".srm", ".sav", ".dsv" })
        {
            var match = Directory.EnumerateFiles(savesFolder, baseName + ext, options)
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .FirstOrDefault();
            if (match is not null) return match;
        }

        // 2. Neben der ROM (z. B. von anderen Emulatoren übernommen) – nur für den Standard-Benutzer,
        //    damit neue Benutzer mit einem eigenen, frischen Spielstand beginnen.
        if (profile == AppSettings.DefaultProfile && Path.GetDirectoryName(game.RomPath) is { } romDir)
            foreach (var ext in new[] { ".srm", ".sav" })
            {
                var candidate = Path.Combine(romDir, baseName + ext);
                if (File.Exists(candidate)) return candidate;
            }

        return null;
    }

    /// <summary>Wo DexHub den Spielstand eines Benutzers selbst ablegt.</summary>
    public static string OwnSavePath(GameEntry game, string profile) =>
        Path.Combine(AppSettings.SavesFolderFor(profile), Path.GetFileNameWithoutExtension(game.RomPath) + ".srm");
}
