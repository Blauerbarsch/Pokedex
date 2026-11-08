namespace DexHub.Core.Pokedex;

/// <summary>Eine Pokémon-Art (unabhängig von Formen).</summary>
public sealed record PokemonSpecies(int Id, string NameDe, string NameEn, int Generation);

/// <summary>Eine Zeile im Pokédex der Oberfläche.</summary>
public sealed class DexEntry
{
    public required PokemonSpecies Species { get; init; }

    /// <summary>Spiele, in denen dieses Pokémon gefangen wurde.</summary>
    public List<string> CaughtIn { get; } = [];

    /// <summary>Spiele, in denen dieses Pokémon nur gesehen wurde.</summary>
    public List<string> SeenIn { get; } = [];

    public int Id => Species.Id;
    public string Number => $"#{Species.Id:0000}";
    public string Name => Species.NameDe;
    public string GenerationText => $"Gen {Species.Generation}";
    public bool IsCaught => CaughtIn.Count > 0;
    public string CaughtMarker => IsCaught ? "●" : SeenIn.Count > 0 ? "○" : "";

    public string DetailText
    {
        get
        {
            var lines = new List<string> { $"Englisch: {Species.NameEn}", $"Generation {Species.Generation}" };
            if (CaughtIn.Count > 0) lines.Add("Gefangen in: " + string.Join(", ", CaughtIn));
            var onlySeen = SeenIn.Except(CaughtIn).ToList();
            if (onlySeen.Count > 0) lines.Add("Gesehen in: " + string.Join(", ", onlySeen));
            if (CaughtIn.Count == 0 && onlySeen.Count == 0) lines.Add("In keinem Spielstand eingetragen.");
            return string.Join("\n", lines);
        }
    }
}
