using DexHub.Core.Models;
using DexHub.Core.Saves;

namespace DexHub.Core.Pokedex;

/// <summary>Verbindet die Pokédex-Daten mit den Spielständen aus der Bibliothek.</summary>
public static class PokedexService
{
    /// <param name="profile">Benutzer – für den Live-Pokédex laufender Spiele.</param>
    public static List<DexEntry> Build(IEnumerable<PokemonSpecies> species, IEnumerable<GameEntry> games, string? profile = null)
    {
        var entries = species.Select(s => new DexEntry { Species = s }).ToDictionary(e => e.Id);

        foreach (var game in games)
        {
            var progress = game.SavePath is null ? null : SaveReader.Read(game.SavePath, game.Info.SaveFormat);

            // Läuft das Spiel gerade, zählt zusätzlich, was schon im Arbeitsspeicher steht
            if (profile is not null && Live.LiveDexStore.Read(profile, game.RomPath) is { } live)
                progress = progress is null ? live
                    : new DexProgress(progress.Caught.Union(live.Caught).ToHashSet(), progress.Seen.Union(live.Seen).ToHashSet());
            if (progress is null) continue;

            foreach (var id in progress.Caught)
                if (entries.TryGetValue(id, out var e)) e.CaughtIn.Add(game.Title);
            foreach (var id in progress.Seen)
                if (entries.TryGetValue(id, out var e)) e.SeenIn.Add(game.Title);
        }

        return entries.Values.OrderBy(e => e.Id).ToList();
    }
}
