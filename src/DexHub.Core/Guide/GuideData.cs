namespace DexHub.Core.Guide;

public sealed record TypeInfo(int Id, string Identifier, string Name);

public sealed record AbilityInfo(int Id, string Name, string Description);

public sealed record MoveInfo(
    int Id, string Name, int Type, int? Power, int? Accuracy, int? Pp, int Priority,
    int DamageClass, int Generation, string Description)
{
    public string DamageClassName => DamageClass switch { 1 => "Status", 2 => "Physisch", 3 => "Speziell", _ => "–" };
}

public sealed record VersionGroupInfo(int Id, string Identifier, string Name, int Generation, int Order);

/// <summary>Wie ein Pokémon eine Attacke lernt (kompakt gespeichert – es gibt hunderttausende Einträge).</summary>
public readonly record struct LearnEntry(short VersionGroup, short Move, byte Method, byte Level);

public sealed record EvolutionStep(int From, int To, string Condition);

/// <summary>Alles, was der Guide über ein Pokémon weiß.</summary>
public sealed class GuidePokemon
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public string NameEn { get; init; } = "";
    public string Genus { get; init; } = "";
    public string FlavorText { get; set; } = "";
    public int Generation { get; init; }
    public int[] Types { get; set; } = [];
    /// <summary>Frühere Typen: bis einschließlich Generation X galten diese (z. B. Pixi vor Gen 6 = Normal).</summary>
    public List<(int UntilGeneration, int[] Types)> PastTypes { get; } = [];
    public int[] BaseStats { get; } = new int[6];
    public int[] EvYield { get; } = new int[6];
    public List<(int Ability, bool Hidden)> Abilities { get; } = [];
    public int Height { get; set; }       // in Dezimetern
    public int Weight { get; set; }       // in Hektogramm
    public int GenderRate { get; init; }  // -1 = geschlechtslos, sonst Achtel weiblich
    public int CaptureRate { get; init; }
    public int BaseHappiness { get; init; }
    public int HatchCounter { get; init; }
    public int GrowthRate { get; init; }
    public int EvolutionChain { get; init; }
    public int EvolvesFrom { get; init; }

    public int BaseStatTotal => BaseStats.Sum();

    /// <summary>Typen in einer bestimmten Generation.</summary>
    public int[] TypesIn(int generation)
    {
        foreach (var (until, types) in PastTypes.OrderBy(p => p.UntilGeneration))
            if (generation <= until) return types;
        return Types;
    }
}

/// <summary>Alle Daten für den Guide, aus dem PokéAPI-Datensatz.</summary>
public sealed class GuideData
{
    public const int Fairy = 18, Steel = 9, Ghost = 8, Dark = 17, Bug = 7, Poison = 4, Psychic = 14, Ice = 15, Fire = 10;

    public Dictionary<int, TypeInfo> Types { get; } = [];
    public Dictionary<(int Attack, int Defense), int> Efficacy { get; } = []; // 0, 50, 100, 200
    public Dictionary<int, GuidePokemon> Pokemon { get; } = [];
    public Dictionary<int, AbilityInfo> Abilities { get; } = [];
    public Dictionary<int, MoveInfo> Moves { get; } = [];
    public List<EvolutionStep> Evolutions { get; } = [];
    public List<VersionGroupInfo> VersionGroups { get; } = [];
    public Dictionary<int, List<LearnEntry>> Learnsets { get; } = [];

    public static readonly string[] StatNames = ["KP", "Angriff", "Verteidigung", "Sp.-Angriff", "Sp.-Vert.", "Initiative"];

    public static readonly Dictionary<string, string> TypeColors = new()
    {
        ["normal"] = "#A8A77A", ["fire"] = "#EE8130", ["water"] = "#6390F0", ["electric"] = "#F7D02C",
        ["grass"] = "#7AC74C", ["ice"] = "#96D9D6", ["fighting"] = "#C22E28", ["poison"] = "#A33EA1",
        ["ground"] = "#E2BF65", ["flying"] = "#A98FF3", ["psychic"] = "#F95587", ["bug"] = "#A6B91A",
        ["rock"] = "#B6A136", ["ghost"] = "#735797", ["dragon"] = "#6F35FC", ["dark"] = "#705746",
        ["steel"] = "#B7B7CE", ["fairy"] = "#D685AD",
    };

    public static string LearnMethodName(int method) => method switch
    {
        1 => "Level-Aufstieg", 2 => "Zucht (Ei-Attacken)", 3 => "Attacken-Lehrer", 4 => "TM / VM", _ => "Sonstige",
    };

    /// <summary>Typen, die es in einer Generation gab (Fee erst ab Gen 6, Unlicht/Stahl ab Gen 2).</summary>
    public IEnumerable<TypeInfo> TypesIn(int generation) =>
        Types.Values.Where(t => !(generation < 6 && t.Id == Fairy) && !(generation < 2 && (t.Id == Steel || t.Id == Dark)))
                    .OrderBy(t => t.Id);

    /// <summary>Schadensfaktor in Prozent (0, 25, 50, 100, 200, 400) inkl. älterer Generationen.</summary>
    public int Effectiveness(int attack, int defense, int generation)
    {
        int factor = Efficacy.GetValueOrDefault((attack, defense), 100);

        // Unterschiede zur heutigen Tabelle
        if (generation <= 5 && defense == Steel && (attack == Ghost || attack == Dark)) factor = 50;
        if (generation == 1)
        {
            if (attack == Ghost && defense == Psychic) factor = 0; // berühmter Fehler in Rot/Blau
            if (attack == Bug && defense == Poison) factor = 200;
            if (attack == Poison && defense == Bug) factor = 200;
            if (attack == Ice && defense == Fire) factor = 100;
        }
        return factor;
    }

    public int Effectiveness(int attack, IEnumerable<int> defenseTypes, int generation)
    {
        double total = 1;
        foreach (var d in defenseTypes) total *= Effectiveness(attack, d, generation) / 100.0;
        return (int)Math.Round(total * 100);
    }

    /// <summary>Alle Arten der Entwicklungsreihe, in der Reihenfolge der Entwicklung.</summary>
    public List<int> ChainOf(int species)
    {
        if (!Pokemon.TryGetValue(species, out var p)) return [species];
        return Pokemon.Values.Where(x => x.EvolutionChain == p.EvolutionChain)
                      .OrderBy(x => Depth(x.Id)).ThenBy(x => x.Id).Select(x => x.Id).ToList();
    }

    private int Depth(int species)
    {
        int depth = 0;
        while (Pokemon.TryGetValue(species, out var p) && p.EvolvesFrom != 0 && depth < 5)
        {
            species = p.EvolvesFrom;
            depth++;
        }
        return depth;
    }
}
