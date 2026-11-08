namespace DexHub.Core.Pokemon;

/// <summary>Ein einzelnes Pokémon aus einem Spielstand (Team oder PC-Box).</summary>
public sealed class OwnedPokemon
{
    public required int Species { get; init; }

    /// <summary>Spitzname, oder null, wenn keiner vergeben wurde.</summary>
    public string? Nickname { get; init; }

    public required uint Exp { get; init; }

    /// <summary>Wird aus den Erfahrungspunkten berechnet (siehe GameData).</summary>
    public int Level { get; set; }

    public required uint Pid { get; init; }
    public ushort TrainerId { get; init; }
    public ushort SecretId { get; init; }
    public string TrainerName { get; init; } = "";

    /// <summary>Schillernd ("Shiny"): Formel aus Gen 3–5.</summary>
    public bool IsShiny => (TrainerId ^ SecretId ^ (Pid >> 16) ^ (Pid & 0xFFFF)) < 8;

    /// <summary>Wesen 0–24 in Spiel-Reihenfolge (0 = Robust).</summary>
    public int Nature => (int)(Pid % 25);

    /// <summary>"♂", "♀" oder "" (geschlechtslos).</summary>
    public string Gender { get; init; } = "";

    public int[] Moves { get; init; } = [];

    /// <summary>Individuelle Werte (0–31): KP, Angriff, Verteidigung, Initiative, Sp.-Angriff, Sp.-Verteidigung.</summary>
    public int[] Ivs { get; init; } = [];

    /// <summary>Fleißpunkte, gleiche Reihenfolge wie Ivs.</summary>
    public int[] Evs { get; init; } = [];

    public int Ball { get; init; }
    public int HeldItem { get; init; }
    public int Friendship { get; init; }
    public DateOnly? MetDate { get; init; }
    public int MetLevel { get; init; }
    public bool IsEgg { get; init; }

    /// <summary>Aus welchem Spiel (Titel aus der Bibliothek).</summary>
    public required string GameTitle { get; init; }

    /// <summary>"Team" oder "Box 3".</summary>
    public required string Location { get; init; }
}
