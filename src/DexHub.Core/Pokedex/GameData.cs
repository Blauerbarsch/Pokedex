namespace DexHub.Core.Pokedex;

/// <summary>
/// Spieldaten, die man zum Anzeigen einzelner Pokémon braucht: Attacken- und Wesensnamen,
/// Level-Berechnung aus Erfahrungspunkten. Stammen aus dem PokéAPI-Datensatz.
/// </summary>
public sealed class GameData
{
    public Dictionary<int, string> MoveNames { get; init; } = [];

    /// <summary>Wesensnamen in Spiel-Reihenfolge (Index = PID % 25).</summary>
    public string[] NatureNames { get; init; } = new string[25];

    /// <summary>Wachstumskurve pro Pokémon-Art.</summary>
    public Dictionary<int, int> GrowthRateBySpecies { get; init; } = [];

    /// <summary>Wachstumskurve -> benötigte EP für Level 1–100 (Index = Level).</summary>
    public Dictionary<int, int[]> ExpTables { get; init; } = [];

    /// <summary>
    /// Interne Nummern der Spiele -> Pokédex-Nummer. Gen 1 und Gen 3 nutzen intern eine andere
    /// Reihenfolge (z. B. ist in Rot/Blau Rhyhorn die Nr. 1). Schlüssel: (Version, interne Nummer).
    /// </summary>
    public Dictionary<(int Version, int Index), int> GameIndices { get; init; } = [];

    public const int VersionRed = 1, VersionRuby = 7;

    public int SpeciesFromGen1Index(int index) => GameIndices.GetValueOrDefault((VersionRed, index));

    public int SpeciesFromGen3Index(int index) =>
        GameIndices.TryGetValue((VersionRuby, index), out var species) ? species : index <= 251 ? index : 0;

    /// <summary>Umkehrung: Pokédex-Nummer -> interne Gen-3-Nummer (0, wenn unbekannt).</summary>
    public int Gen3IndexFromSpecies(int species)
    {
        if (species <= 251) return species;
        foreach (var ((version, index), s) in GameIndices)
            if (version == VersionRuby && s == species) return index;
        return 0;
    }

    public static readonly string[] BallNames =
    [
        "", "Meisterball", "Hyperball", "Superball", "Pokéball", "Safariball", "Netzball", "Tauchball",
        "Nestball", "Wiederball", "Timerball", "Luxusball", "Premierball", "Finsterball", "Heilball",
        "Flottball", "Jubelball", "Turboball", "Levelball", "Köderball", "Schwerball", "Sympaball",
        "Freundesball", "Mondball", "Turnierball", "Parkball",
    ];

    public static readonly string[] StatNames = ["KP", "Angr.", "Vert.", "Init.", "Sp.-Angr.", "Sp.-Vert."];

    public string Move(int id) => id == 0 ? "" : MoveNames.GetValueOrDefault(id, $"Attacke #{id}");

    public string Nature(int index) =>
        index >= 0 && index < NatureNames.Length && !string.IsNullOrEmpty(NatureNames[index]) ? NatureNames[index] : $"Wesen {index}";

    public static string Ball(int id) => id > 0 && id < BallNames.Length ? BallNames[id] : $"Ball {id}";

    /// <summary>Höchstes Level, dessen EP-Grenze erreicht ist.</summary>
    public int LevelFromExp(int species, uint exp)
    {
        if (!GrowthRateBySpecies.TryGetValue(species, out var rate) || !ExpTables.TryGetValue(rate, out var table))
            return 0;
        int level = 1;
        for (int l = 1; l <= 100; l++)
            if (table[l] <= exp) level = l;
        return level;
    }
}
