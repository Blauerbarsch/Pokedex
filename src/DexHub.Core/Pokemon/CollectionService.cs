using DexHub.Core.Models;
using DexHub.Core.Pokedex;
using DexHub.Core.Saves;

namespace DexHub.Core.Pokemon;

/// <summary>Sammelt alle einzelnen Pokémon aus allen Spielständen der Bibliothek.</summary>
public static class CollectionService
{
    public static List<OwnedPokemon> Load(IEnumerable<GameEntry> games, GameData? gameData)
    {
        var result = new List<OwnedPokemon>();
        foreach (var game in games)
        {
            if (game.SavePath is null || !Gen4PokemonReader.IsSupported(game.Info.SaveFormat)) continue;

            byte[] data;
            try { data = File.ReadAllBytes(game.SavePath); }
            catch (IOException) { continue; }

            int partition = SaveReader.FindNewestGen4Partition(data, game.Info.SaveFormat);
            if (partition < 0) continue;

            result.AddRange(Gen4PokemonReader.Read(data, game.Info.SaveFormat, partition, game.Title));
        }

        if (gameData is not null)
            foreach (var p in result) p.Level = gameData.LevelFromExp(p.Species, p.Exp);

        return result;
    }
}
