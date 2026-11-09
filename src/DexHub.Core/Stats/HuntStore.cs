using System.Text.Json;
using DexHub.Core.Settings;

namespace DexHub.Core.Stats;

/// <summary>Eine Shiny-Jagd: wie oft man einem Pokémon begegnet ist, bis es schillernd kam.</summary>
public sealed class Hunt
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Profile { get; set; } = "";
    /// <summary>Dateiname der ROM (wie bei der Spielzeit).</summary>
    public string Rom { get; set; } = "";
    public string GameTitle { get; set; } = "";
    /// <summary>Gesuchte Art (0 = noch nicht festgelegt).</summary>
    public int Species { get; set; }
    public int Count { get; set; }
    public int Generation { get; set; } = 4;
    public DateTime Started { get; set; } = DateTime.Now;
    public DateTime? Found { get; set; }

    public bool IsActive => Found is null;

    /// <summary>Grundchance je Begegnung: 1/8192 bis Gen 5 (Gen 2 über DVs ebenso), danach 1/4096.</summary>
    public double Odds => Generation >= 6 ? 1 / 4096.0 : 1 / 8192.0;

    /// <summary>Wahrscheinlichkeit, nach so vielen Begegnungen schon mindestens ein Shiny gesehen zu haben.</summary>
    public double ChanceSoFar => 1 - Math.Pow(1 - Odds, Count);
}

/// <summary>
/// Shiny-Jagden aller Benutzer. Spiel-Prozesse und Hauptfenster schreiben gleichzeitig –
/// deshalb wie bei der Spielzeit kurz sperren und wiederholen.
/// </summary>
public static class HuntStore
{
    private static string FilePath => Path.Combine(AppSettings.AppDataFolder, "hunts.json");

    public static List<Hunt> LoadAll()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<Hunt>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return [];
    }

    public static List<Hunt> For(string profile) => LoadAll().Where(h => h.Profile == profile).ToList();

    /// <summary>Laufende Jagd in diesem Spiel (die zuletzt begonnene), oder null.</summary>
    public static Hunt? Active(string profile, string romPath) =>
        For(profile).Where(h => h.IsActive && h.Rom == Path.GetFileName(romPath)).OrderByDescending(h => h.Started).FirstOrDefault();

    /// <summary>Zähler der laufenden Jagd ändern; legt eine neue Jagd an, wenn es keine gibt.</summary>
    public static Hunt? Change(string profile, string romPath, string gameTitle, int generation, int delta)
    {
        Hunt? result = null;
        Update(all =>
        {
            var hunt = all.Where(h => h.Profile == profile && h.IsActive && h.Rom == Path.GetFileName(romPath))
                          .OrderByDescending(h => h.Started).FirstOrDefault();
            if (hunt is null)
            {
                if (delta <= 0) return;
                hunt = new Hunt { Profile = profile, Rom = Path.GetFileName(romPath), GameTitle = gameTitle, Generation = generation };
                all.Add(hunt);
            }
            hunt.Count = Math.Max(0, hunt.Count + delta);
            result = hunt;
        });
        return result;
    }

    /// <summary>Eine Jagd bearbeiten (Art festlegen, als gefunden markieren …).</summary>
    public static void Edit(string id, Action<Hunt> change) => Update(all =>
    {
        if (all.FirstOrDefault(h => h.Id == id) is { } hunt) change(hunt);
    });

    public static void Add(Hunt hunt) => Update(all => all.Add(hunt));

    public static void Delete(string id) => Update(all => all.RemoveAll(h => h.Id == id));

    private static void Update(Action<List<Hunt>> change)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var lockFile = new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var all = LoadAll();
                change(all);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(all));
                File.Move(tmp, FilePath, overwrite: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
    }
}
