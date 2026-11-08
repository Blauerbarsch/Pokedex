using System.Buffers.Binary;
using DexHub.Core.Models;
using DexHub.Core.Pokemon;
using DexHub.Core.Saves;
using DexHub.Core.Settings;

namespace DexHub.Core.Trading;

/// <summary>Eine Tauschseite: wer, welches Spiel, welcher Spielstand.</summary>
public sealed record TradeSide(string Profile, GameEntry Game);

/// <summary>Ein ausgewähltes Pokémon in einer Box.</summary>
public sealed record TradeSlot(int Box, int Slot, OwnedPokemon Pokemon);

/// <summary>
/// Tauscht Pokémon direkt in den Spielständen zweier Spiele/Benutzer.
/// Vorher werden beide Spielstände gesichert; Tausch-Entwicklungen werden nachgebildet.
/// </summary>
public static class TradeService
{
    /// <summary>Gen-4-Item-Nummern der Tausch-Gegenstände.</summary>
    private const int KingsRock = 221, DeepSeaTooth = 226, DeepSeaScale = 227, MetalCoat = 233,
        DragonScale = 235, UpGrade = 252, Protector = 321, Electirizer = 322, Magmarizer = 323,
        DubiousDisc = 324, ReaperCloth = 325, Everstone = 229;

    /// <summary>(Art, benötigtes Item oder 0) -> neue Art.</summary>
    private static readonly Dictionary<(int Species, int Item), int> TradeEvolutions = new()
    {
        [(64, 0)] = 65,               // Kadabra -> Simsala
        [(67, 0)] = 68,               // Maschock -> Machomei
        [(75, 0)] = 76,               // Georok -> Geowaz
        [(93, 0)] = 94,               // Alpollo -> Gengar
        [(61, KingsRock)] = 186,      // Quaputzi -> Quaxo
        [(79, KingsRock)] = 199,      // Flegmon -> Laschoking
        [(95, MetalCoat)] = 208,      // Onix -> Stahlos
        [(123, MetalCoat)] = 212,     // Sichlor -> Scherox
        [(117, DragonScale)] = 230,   // Seemon -> Seedraking
        [(137, UpGrade)] = 233,       // Porygon -> Porygon2
        [(366, DeepSeaTooth)] = 367,  // Perlu -> Aalabyss
        [(366, DeepSeaScale)] = 368,  // Perlu -> Saganabyss
        [(112, Protector)] = 464,     // Rizeros -> Rihornior
        [(125, Electirizer)] = 466,   // Elektek -> Elevoltek
        [(126, Magmarizer)] = 467,    // Magmar -> Magbrant
        [(233, DubiousDisc)] = 474,   // Porygon2 -> Porygon-Z
        [(356, ReaperCloth)] = 477,   // Zwirrklop -> Zwirrfinst
    };

    /// <summary>Lädt den Spielstand einer Seite (eigener Spielstand des Benutzers bevorzugt).</summary>
    public static Gen4Save? Load(TradeSide side, out string error)
    {
        var path = SaveLocator.Find(side.Game, side.Profile);
        if (path is null)
        {
            error = $"{side.Profile} hat noch keinen Spielstand für {side.Game.Title}.";
            return null;
        }
        return Gen4Save.Load(path, side.Game.Info.SaveFormat, side.Game.Title, out error);
    }

    /// <summary>Führt den Tausch aus. Rückgabe: Beschreibung für den Nutzer.</summary>
    public static string Trade(TradeSide sideA, TradeSlot slotA, TradeSide sideB, TradeSlot slotB,
        bool tradeEvolutions, Func<int, string> speciesName)
    {
        var saveA = Load(sideA, out var errorA) ?? throw new InvalidOperationException(errorA);
        var saveB = Load(sideB, out var errorB) ?? throw new InvalidOperationException(errorB);
        bool sameSave = SaveLocator.Find(sideA.Game, sideA.Profile) == SaveLocator.Find(sideB.Game, sideB.Profile);
        if (sameSave) throw new InvalidOperationException("Beide Seiten nutzen denselben Spielstand – bitte zwei verschiedene wählen.");

        var rawA = saveA.GetRaw(slotA.Box, slotA.Slot);
        var rawB = saveB.GetRaw(slotB.Box, slotB.Slot);

        // Sicherheitsprüfung: sind an den Plätzen noch die ausgewählten Pokémon?
        if (Gen4PokemonReader.Parse(rawA, "", "")?.Pid != slotA.Pokemon.Pid ||
            Gen4PokemonReader.Parse(rawB, "", "")?.Pid != slotB.Pokemon.Pid)
            throw new InvalidOperationException("Die Boxen haben sich inzwischen geändert. Bitte neu auswählen.");

        // Sicherungen
        Backup(sideA);
        Backup(sideB);

        var notes = new List<string>();
        var (newForB, speciesForB) = Evolve(rawA, tradeEvolutions, speciesName, notes);
        var (newForA, speciesForA) = Evolve(rawB, tradeEvolutions, speciesName, notes);

        saveA.SetRaw(slotA.Box, slotA.Slot, newForA);
        saveB.SetRaw(slotB.Box, slotB.Slot, newForB);
        saveA.RegisterCaught(slotB.Pokemon.Species);
        saveA.RegisterCaught(speciesForA);
        saveB.RegisterCaught(slotA.Pokemon.Species);
        saveB.RegisterCaught(speciesForB);

        // Immer in den eigenen Spielstand des Benutzers schreiben (Originale neben der ROM bleiben unberührt)
        saveA.Save(SaveLocator.OwnSavePath(sideA.Game, sideA.Profile));
        saveB.Save(SaveLocator.OwnSavePath(sideB.Game, sideB.Profile));

        // Automatische Sicherung beim Fortsetzen würde den Tausch sonst rückgängig machen
        RemoveAutoState(sideA);
        RemoveAutoState(sideB);

        var summary = $"{speciesName(slotA.Pokemon.Species)} ({sideA.Profile}) ⇄ {speciesName(slotB.Pokemon.Species)} ({sideB.Profile})";
        return notes.Count == 0 ? summary : summary + "\n" + string.Join("\n", notes);
    }

    /// <summary>Tausch-Entwicklung anwenden (falls zutreffend). Gibt neue Rohdaten und Art zurück.</summary>
    private static (byte[] Raw, int Species) Evolve(byte[] raw, bool enabled, Func<int, string> speciesName, List<string> notes)
    {
        var d = Gen4PokemonReader.Decrypt(raw);
        if (d is null) return (raw, 0);
        int species = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(0x08));
        if (!enabled) return (raw, species);

        int item = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(0x0A));
        bool isEgg = (BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(0x38)) >> 30 & 1) == 1;
        if (isEgg || item == Everstone) return (raw, species); // Ewigstein verhindert Entwicklungen

        if (!TradeEvolutions.TryGetValue((species, item), out var evolved) &&
            !TradeEvolutions.TryGetValue((species, 0), out evolved))
            return (raw, species);

        bool usedItem = TradeEvolutions.ContainsKey((species, item)) && item != 0;
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(0x08), (ushort)evolved);
        if (usedItem) BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(0x0A), 0); // Item wird verbraucht

        // Ohne Spitznamen steht der Artname im Namensfeld – den aktualisieren
        bool nicknamed = (BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(0x38)) >> 31 & 1) == 1;
        if (!nicknamed)
            Gen4PokemonReader.EncodeString(speciesName(evolved), 22).CopyTo(d.AsSpan(0x48));

        notes.Add($"✨ {speciesName(species)} hat sich beim Tausch zu {speciesName(evolved)} entwickelt!");
        return (Gen4PokemonReader.Encrypt(d), evolved);
    }

    private static void Backup(TradeSide side)
    {
        var source = SaveLocator.Find(side.Game, side.Profile);
        if (source is null) return;
        var name = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{AppSettings.SafeName(side.Profile)}_{Path.GetFileName(source)}";
        File.Copy(source, Path.Combine(AppSettings.BackupsFolder, name));
    }

    private static void RemoveAutoState(TradeSide side)
    {
        var state = Path.Combine(AppSettings.StatesFolderFor(side.Profile),
            Path.GetFileNameWithoutExtension(side.Game.RomPath) + ".auto.state");
        if (!File.Exists(state)) return;
        var target = Path.Combine(AppSettings.BackupsFolder,
            $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{AppSettings.SafeName(side.Profile)}_{Path.GetFileName(state)}");
        File.Move(state, target);
    }
}
