using DexHub.Core.Models;
using DexHub.Core.Saves;

namespace DexHub.Core.Pokedex;

/// <summary>Verbindet die Pokédex-Daten mit den Spielständen aus der Bibliothek.</summary>
public static class PokedexService
{
    public static List<DexEntry> Build(IEnumerable<PokemonSpecies> species, IEnumerable<GameEntry> games)
    {
        var entries = species.Select(s => new DexEntry { Species = s }).ToDictionary(e => e.Id);

        foreach (var game in games)
        {
            if (game.SavePath is null) continue;
            var progress = SaveReader.Read(game.SavePath, game.Info.SaveFormat);
            if (progress is null) continue;

            foreach (var id in progress.Caught)
                if (entries.TryGetValue(id, out var e)) e.CaughtIn.Add(game.Title);
            foreach (var id in progress.Seen)
                if (entries.TryGetValue(id, out var e)) e.SeenIn.Add(game.Title);
        }

        return entries.Values.OrderBy(e => e.Id).ToList();
    }
}
