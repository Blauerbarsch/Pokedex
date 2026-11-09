using System.Text.Json;
using DexHub.Core.Pokedex;
using DexHub.Core.Pokemon;
using DexHub.Core.Saves;
using DexHub.Core.Settings;

namespace DexHub.Core.Trading;

/// <summary>Ein Pokémon in der Bank: die Original-Rohdaten (Gen 3: 80 Byte, Gen 4: 136 Byte, verschlüsselt).</summary>
public sealed class BankEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int Generation { get; set; }
    public string Raw { get; set; } = "";
    public string FromGame { get; set; } = "";
    public DateTime StoredAt { get; set; } = DateTime.Now;

    public byte[] RawBytes => System.Convert.FromBase64String(Raw);

    /// <summary>Für die Anzeige: das Pokémon entschlüsselt.</summary>
    public OwnedPokemon? Parse(GameData gameData) => Generation switch
    {
        3 => Gen3Save.Parse(RawBytes, FromGame, "Bank", gameData.SpeciesFromGen3Index),
        5 => Gen5PokemonReader.Parse(RawBytes, FromGame, "Bank"),
        _ => Gen4PokemonReader.Parse(RawBytes, FromGame, "Bank"),
    };
}

/// <summary>
/// Pokémon-Bank: Pokémon aus einem Spielstand herausnehmen und später in ein anderes Spiel legen –
/// auch von Gen 3 nach Gen 4 (wie der Pal-Park). Jeder Benutzer hat seine eigene Bank.
/// Vor jeder Änderung wird der Spielstand gesichert.
/// </summary>
public static class PokemonBank
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly object Lock = new();

    private static string FileFor(string profile) => Path.Combine(AppSettings.BankFolderFor(profile), "bank.json");

    public static List<BankEntry> Load(string profile)
    {
        var path = FileFor(profile);
        if (!File.Exists(path)) return [];
        try { return JsonSerializer.Deserialize<List<BankEntry>>(File.ReadAllText(path), Json) ?? []; }
        catch (JsonException ex)
        {
            Console.WriteLine("[DexHub] Bank nicht lesbar: " + ex.Message);
            return [];
        }
    }

    private static void Store(string profile, List<BankEntry> entries)
    {
        var path = FileFor(profile);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(entries, Json));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Pokémon aus dem Spiel in die Bank legen. Der Box-Platz im Spiel wird geleert.</summary>
    public static BankEntry Deposit(TradeSide side, TradeSlot slot, GameData gameData)
    {
        lock (Lock)
        {
            var save = TradeService.Load(side, gameData, out var error) ?? throw new InvalidOperationException(error);
            var raw = save.GetRaw(slot.Box, slot.Slot);
            if (save.PidOf(raw) != slot.Pokemon.Pid)
                throw new InvalidOperationException("Die Boxen haben sich inzwischen geändert. Bitte neu auswählen.");

            TradeService.Backup(side);
            var entry = new BankEntry { Generation = save.Generation, Raw = System.Convert.ToBase64String(raw), FromGame = side.Game.Title };

            // Erst die Bank schreiben, dann das Spiel – so geht im Fehlerfall nichts verloren (höchstens doppelt)
            var entries = Load(side.Profile);
            entries.Add(entry);
            Store(side.Profile, entries);

            save.Clear(slot.Box, slot.Slot);
            save.Save(SaveLocator.OwnSavePath(side.Game, side.Profile));
            TradeService.RemoveAutoState(side);
            return entry;
        }
    }

    /// <summary>
    /// Pokémon aus der Bank in den ersten freien Box-Platz des Spiels legen.
    /// Gen 3 → Gen 4 wird umgewandelt; Gen 4 → Gen 3 geht nicht (wie im Original).
    /// </summary>
    /// <returns>Hinweise für den Nutzer (z. B. "Item bleibt zurück").</returns>
    public static List<string> Withdraw(string profile, string entryId, TradeSide target, GameData gameData,
        Func<int, SpeciesInfo?> speciesInfo, SpeciesNames names)
    {
        lock (Lock)
        {
            var entries = Load(profile);
            var entry = entries.FirstOrDefault(e => e.Id == entryId)
                        ?? throw new InvalidOperationException("Dieses Pokémon ist nicht mehr in der Bank.");
            var save = TradeService.Load(target, gameData, out var error) ?? throw new InvalidOperationException(error);
            var notes = new List<string>();

            byte[] raw = entry.RawBytes;
            if (entry.Generation > save.Generation)
                throw new InvalidOperationException($"Pokémon aus Gen {entry.Generation} können nicht zurück nach Gen {save.Generation} – das ging auch im Original nicht.");
            int generation = entry.Generation;
            if (generation == 3 && save.Generation >= 4)
            {
                raw = PalPark.Convert(raw, gameData.SpeciesFromGen3Index, speciesInfo, gameData.LevelFromExp, names, notes);
                notes.Insert(0, "Über den Pal-Park nach Gen 4 gebracht.");
                generation = 4;
            }
            if (generation == 4 && save.Generation == 5)
            {
                raw = PokeTransfer.Convert(raw, gameData.LevelFromExp, notes);
                notes.Add("Per PokéTransfer nach Gen 5 gebracht.");
                generation = 5;
            }

            var parsed = generation == 5 ? Gen5PokemonReader.Parse(raw, "", "")
                       : generation == 4 ? Gen4PokemonReader.Parse(raw, "", "")
                       : entry.Parse(gameData);
            if (parsed is null) throw new InvalidOperationException("Die Pokémon-Daten sind beschädigt.");

            var free = save.FirstFreeSlot() ?? throw new InvalidOperationException("Alle Boxen in diesem Spiel sind voll.");
            TradeService.Backup(target);
            save.SetRaw(free.Box, free.Slot, raw);
            save.RegisterCaught(parsed.Species);
            save.Save(SaveLocator.OwnSavePath(target.Game, target.Profile));
            TradeService.RemoveAutoState(target);

            entries.Remove(entry);
            Store(profile, entries);
            notes.Add($"Liegt jetzt in Box {free.Box + 1}, Platz {free.Slot + 1}.");
            return notes;
        }
    }
}
