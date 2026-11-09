using DexHub.Core.Models;
using DexHub.Core.Pokedex;
using DexHub.Core.Saves;

namespace DexHub.Core.Pokemon;

/// <summary>Sammelt alle einzelnen Pokémon aus allen Spielständen der Bibliothek (Gen 1–4).</summary>
public static class CollectionService
{
    public static List<OwnedPokemon> Load(IEnumerable<GameEntry> games, GameData? gameData)
    {
        var result = new List<OwnedPokemon>();
        foreach (var game in games)
        {
            if (game.SavePath is null) continue;
            var format = game.Info.SaveFormat;

            byte[] data;
            try { data = File.ReadAllBytes(game.SavePath); }
            catch (IOException) { continue; }

            try
            {
                result.AddRange(ReadGame(data, format, game.Title, gameData));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DexHub] {game.Title}: Pokémon nicht lesbar ({ex.Message})");
            }
        }

        if (gameData is not null)
            foreach (var p in result.Where(p => p.Exp > 0)) p.Level = gameData.LevelFromExp(p.Species, p.Exp);

        return result;
    }

    private static IEnumerable<OwnedPokemon> ReadGame(byte[] data, SaveFormat format, string title, GameData? gameData)
    {
        // Gen 1 und Gen 3 brauchen die Umrechnung interner Nummern -> Pokédex-Nummer
        if (Gen12PokemonReader.IsSupported(format))
        {
            if (format == SaveFormat.Gen1 && gameData is null) return [];
            return Gen12PokemonReader.Read(data, format, title,
                format == SaveFormat.Gen1 ? gameData!.SpeciesFromGen1Index : index => index);
        }

        if (format == SaveFormat.Gen3)
        {
            if (gameData is null) return [];
            return Gen3Save.Load(data)?.ReadPokemon(title, gameData.SpeciesFromGen3Index) ?? [];
        }

        if (Gen4PokemonReader.IsSupported(format))
        {
            // Bevorzugt: Kopien über Prüfsummen und Speicherzähler bestimmen (genau wie das Spiel)
            var checkedSave = Trading.Gen4Save.Load(data, format, title, out _);
            if (checkedSave is not null)
                return Gen4PokemonReader.Read(data, format, checkedSave.GeneralPartition, title, checkedSave.StoragePartition);

            int partition = SaveReader.FindNewestGen4Partition(data, format);
            return partition < 0 ? [] : Gen4PokemonReader.Read(data, format, partition, title);
        }

        return [];
    }
}
