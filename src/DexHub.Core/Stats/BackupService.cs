using System.IO.Compression;
using DexHub.Core.Settings;

namespace DexHub.Core.Stats;

/// <summary>
/// Automatische Sicherung: Spielstände, Bank, Shiny-Jagden, Spielzeit, Erfolge und Einstellungen aller Benutzer
/// als ZIP in einen frei wählbaren Ordner (z. B. auf dem NAS). Ältere Sicherungen werden aufgeräumt.
/// Schnellspeicherungen (groß) und heruntergeladene Daten (lassen sich neu laden) kommen nicht mit.
/// </summary>
public static class BackupService
{
    public const string Prefix = "DexHub-Sicherung_";

    public static bool IsDue(AppSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.AutoBackupFolder)
        && (settings.LastAutoBackup is not { } last || DateTime.Now - last >= TimeSpan.FromHours(Math.Max(0, settings.AutoBackupHours)));

    /// <summary>Welche Dateien gesichert werden (relativ zum DexHub-Ordner).</summary>
    public static List<string> FilesToBackUp()
    {
        var root = AppSettings.AppDataFolder;
        var result = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.EndsWith(".lock") || rel.EndsWith(".tmp")) continue;
            var parts = rel.Split('/');
            bool inSaves = parts.Contains("saves") || parts.Contains("bank");
            bool settingsFile = rel.EndsWith(".json") &&
                                (parts.Length == 1 || (parts.Length == 3 && parts[0] == "profiles"));
            if (inSaves || settingsFile) result.Add(rel);
        }
        return result;
    }

    /// <summary>Legt eine neue Sicherung an und gibt ihren Pfad zurück.</summary>
    public static string Create(string targetFolder, int keep)
    {
        Directory.CreateDirectory(targetFolder);
        var target = Path.Combine(targetFolder, $"{Prefix}{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.zip");
        var tmp = target + ".tmp";

        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            foreach (var rel in FilesToBackUp())
            {
                try
                {
                    // Datei kann gerade von einem laufenden Spiel geschrieben werden – dann teilen
                    using var source = new FileStream(Path.Combine(AppSettings.AppDataFolder, rel), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var entry = zip.CreateEntry(rel, CompressionLevel.Optimal);
                    using var dest = entry.Open();
                    source.CopyTo(dest);
                }
                catch (IOException ex)
                {
                    Console.WriteLine($"[DexHub] Sicherung: {rel} übersprungen ({ex.Message})");
                }
            }
        }
        File.Move(tmp, target, overwrite: true);
        Prune(targetFolder, keep);
        return target;
    }

    /// <summary>Nur die neuesten <paramref name="keep"/> Sicherungen behalten.</summary>
    public static void Prune(string folder, int keep)
    {
        if (keep <= 0) return;
        var old = Directory.GetFiles(folder, Prefix + "*.zip").OrderByDescending(f => f, StringComparer.Ordinal).Skip(keep);
        foreach (var file in old)
        {
            try { File.Delete(file); }
            catch (IOException ex) { Console.WriteLine("[DexHub] Alte Sicherung nicht löschbar: " + ex.Message); }
        }
    }
}
