using System.Text.Json;
using DexHub.Core.Settings;

namespace DexHub.Core.Stats;

/// <summary>Spielzeit pro Benutzer und Spiel. Mehrere Spiel-Prozesse schreiben – deshalb kurz sperren und wiederholen.</summary>
public static class PlayTimeStore
{
    public sealed record Entry(double Seconds, DateTime LastPlayed);

    private static string FilePath => Path.Combine(AppSettings.AppDataFolder, "playtime.json");

    private static string Key(string profile, string romPath) => profile + "|" + Path.GetFileName(romPath);

    public static Dictionary<string, Entry> LoadAll()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return [];
    }

    public static Entry? Get(string profile, string romPath) => LoadAll().GetValueOrDefault(Key(profile, romPath));

    public static TimeSpan Total(string profile) =>
        TimeSpan.FromSeconds(LoadAll().Where(kv => kv.Key.StartsWith(profile + "|", StringComparison.Ordinal)).Sum(kv => kv.Value.Seconds));

    /// <summary>Spielzeit hinzufügen (liest, ergänzt, schreibt – mit einfachem Wiederholen bei gleichzeitigem Zugriff).</summary>
    public static void Add(string profile, string romPath, double seconds)
    {
        if (seconds <= 0) return;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var lockFile = new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var all = LoadAll();
                var key = Key(profile, romPath);
                var old = all.GetValueOrDefault(key);
                all[key] = new Entry((old?.Seconds ?? 0) + seconds, DateTime.Now);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(all));
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>"3 Std. 12 Min." bzw. "45 Min."</summary>
    public static string Format(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int)time.TotalHours} Std. {time.Minutes} Min." : $"{Math.Max(1, time.Minutes)} Min.";
}
