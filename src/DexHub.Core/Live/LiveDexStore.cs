using System.Text.Json;
using DexHub.Core.Saves;
using DexHub.Core.Settings;

namespace DexHub.Core.Live;

/// <summary>
/// Übergabe des Live-Pokédex vom Spiel-Prozess an das Hauptfenster: eine kleine Datei pro Benutzer und Spiel.
/// Wird beim Beenden des Spiels gelöscht – danach zählt wieder nur, was im Spiel gespeichert wurde.
/// </summary>
public static class LiveDexStore
{
    private sealed record Entry(int[] Caught, int[] Seen, DateTime Updated);

    private static string Folder => Path.Combine(AppSettings.AppDataFolder, "live");

    private static string PathFor(string profile, string romPath) =>
        Path.Combine(Folder, AppSettings.SafeName(profile) + "_" + Path.GetFileNameWithoutExtension(romPath) + ".json");

    public static void Write(string profile, string romPath, DexProgress progress)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var path = PathFor(profile, romPath);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Entry(progress.Caught.Order().ToArray(), progress.Seen.Order().ToArray(), DateTime.Now)));
            File.Move(tmp, path, overwrite: true);
        }
        catch (IOException ex) { Console.WriteLine("[DexHub] Live-Pokédex nicht geschrieben: " + ex.Message); }
    }

    public static DexProgress? Read(string profile, string romPath)
    {
        try
        {
            var path = PathFor(profile, romPath);
            if (!File.Exists(path)) return null;
            var e = JsonSerializer.Deserialize<Entry>(File.ReadAllText(path));
            return e is null ? null : new DexProgress(e.Caught.ToHashSet(), e.Seen.ToHashSet());
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    /// <summary>Reste von abgestürzten Spielen entfernen (beim Programmstart, wenn noch kein Spiel läuft).</summary>
    public static void Clear()
    {
        try
        {
            if (Directory.Exists(Folder))
                foreach (var file in Directory.GetFiles(Folder)) File.Delete(file);
        }
        catch (IOException) { }
    }

    public static void Delete(string profile, string romPath)
    {
        try { File.Delete(PathFor(profile, romPath)); }
        catch (IOException) { }
    }
}
