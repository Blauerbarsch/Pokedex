using System.Text.Json;
using DexHub.Core.Settings;

namespace DexHub.Core.Stats;

/// <summary>Alles, woraus Erfolge berechnet werden.</summary>
public sealed record AchievementContext(
    IReadOnlySet<int> CaughtSpecies,
    int ShinyCount,
    int HighestLevel,
    int PerfectIvPokemon,
    TimeSpan PlayTime,
    int GamesPlayed,
    int HuntEncounters,
    int HuntsFound,
    int BankCount);

/// <summary>Ein Erfolg: Fortschritt als (aktuell, Ziel).</summary>
public sealed record Achievement(string Id, string Icon, string Title, string Description, Func<AchievementContext, (int Current, int Target)> Progress)
{
    public bool IsUnlocked(AchievementContext c) => Progress(c) is var (current, target) && current >= target;
}

public static class Achievements
{
    private static int Range(AchievementContext c, int from, int to) => c.CaughtSpecies.Count(s => s >= from && s <= to);

    public static readonly IReadOnlyList<Achievement> All =
    [
        new("first", "🎒", "Der erste Fang", "Fange dein erstes Pokémon.", c => (c.CaughtSpecies.Count, 1)),
        new("dex50", "📕", "Sammler", "50 verschiedene Arten gefangen.", c => (c.CaughtSpecies.Count, 50)),
        new("dex100", "📗", "Fleißiger Forscher", "100 verschiedene Arten gefangen.", c => (c.CaughtSpecies.Count, 100)),
        new("dex250", "📘", "Pokédex-Profi", "250 verschiedene Arten gefangen.", c => (c.CaughtSpecies.Count, 250)),
        new("dex493", "📙", "Meister des Pokédex", "493 Arten – so viele, wie es bis Gen 4 gibt.", c => (c.CaughtSpecies.Count, 493)),
        new("kanto", "🗻", "Kanto komplett", "Alle 151 Pokémon aus Kanto gefangen.", c => (Range(c, 1, 151), 151)),
        new("johto", "🔔", "Johto komplett", "Alle 100 Pokémon aus Johto gefangen.", c => (Range(c, 152, 251), 100)),
        new("hoenn", "🌋", "Hoenn komplett", "Alle 135 Pokémon aus Hoenn gefangen.", c => (Range(c, 252, 386), 135)),
        new("sinnoh", "❄️", "Sinnoh komplett", "Alle 107 Pokémon aus Sinnoh gefangen.", c => (Range(c, 387, 493), 107)),
        new("shiny1", "✨", "Es glitzert!", "Besitze ein schillerndes Pokémon.", c => (c.ShinyCount, 1)),
        new("shiny5", "🌟", "Shiny-Sammler", "Besitze 5 schillernde Pokémon.", c => (c.ShinyCount, 5)),
        new("hunt", "🔍", "Geduldiger Jäger", "Eine Shiny-Jagd erfolgreich beendet.", c => (c.HuntsFound, 1)),
        new("hunt1000", "⏳", "Durchhaltevermögen", "1.000 Begegnungen auf Shiny-Jagd gezählt.", c => (c.HuntEncounters, 1000)),
        new("lv50", "💪", "Gut trainiert", "Ein Pokémon auf Level 50.", c => (c.HighestLevel, 50)),
        new("lv100", "👑", "Level 100", "Ein Pokémon auf Level 100.", c => (c.HighestLevel, 100)),
        new("ivs", "🧬", "Gute Gene", "Ein Pokémon mit mindestens drei perfekten IVs.", c => (c.PerfectIvPokemon, 1)),
        new("bank", "🏦", "Sicher verwahrt", "Ein Pokémon in die Bank gelegt.", c => (c.BankCount, 1)),
        new("games3", "🎮", "Weltenbummler", "3 verschiedene Spiele gespielt.", c => (c.GamesPlayed, 3)),
        new("games8", "🗺️", "Alle Regionen", "8 verschiedene Spiele gespielt.", c => (c.GamesPlayed, 8)),
        new("time10", "🕙", "Zehn Stunden", "10 Stunden gespielt.", c => ((int)c.PlayTime.TotalHours, 10)),
        new("time100", "🕛", "Hundert Stunden", "100 Stunden gespielt.", c => ((int)c.PlayTime.TotalHours, 100)),
    ];

    private static string FilePath(string profile) => AppSettings.ProfileFile(profile, "achievements.json");

    /// <summary>Wann welcher Erfolg zum ersten Mal erreicht wurde.</summary>
    public static Dictionary<string, DateTime> LoadUnlocked(string profile)
    {
        try
        {
            var path = FilePath(profile);
            if (File.Exists(path)) return JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return [];
    }

    /// <summary>Neu erreichte Erfolge mit heutigem Datum speichern. Rückgabe: die neuen.</summary>
    public static List<Achievement> Update(string profile, AchievementContext context)
    {
        var unlocked = LoadUnlocked(profile);
        var fresh = All.Where(a => !unlocked.ContainsKey(a.Id) && a.IsUnlocked(context)).ToList();
        if (fresh.Count == 0) return fresh;
        foreach (var a in fresh) unlocked[a.Id] = DateTime.Now;
        try { File.WriteAllText(FilePath(profile), JsonSerializer.Serialize(unlocked)); }
        catch (IOException ex) { Console.WriteLine("[DexHub] Erfolge nicht gespeichert: " + ex.Message); }
        return fresh;
    }
}
