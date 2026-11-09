using System.Buffers.Binary;
using DexHub.Core.Models;
using DexHub.Core.Pokedex;
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
    /// <summary>Lädt den Spielstand einer Seite (eigener Spielstand des Benutzers bevorzugt).</summary>
    public static bool IsSupported(SaveFormat format) => format == SaveFormat.Gen3 || Gen4Save.IsSupported(format);

    public static ITradeSave? Load(TradeSide side, GameData gameData, out string error)
    {
        var path = SaveLocator.Find(side.Game, side.Profile);
        if (path is null)
        {
            error = $"{side.Profile} hat noch keinen Spielstand für {side.Game.Title}.";
            return null;
        }
        if (side.Game.Info.SaveFormat != SaveFormat.Gen3)
            return Gen4Save.Load(path, side.Game.Info.SaveFormat, side.Game.Title, out error);

        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (IOException ex) { error = ex.Message; return null; }
        return Gen3TradeSave.Load(data, side.Game.Title, gameData, out error);
    }

    /// <summary>Führt den Tausch aus. Rückgabe: Beschreibung für den Nutzer.</summary>
    public static string Trade(TradeSide sideA, TradeSlot slotA, TradeSide sideB, TradeSlot slotB,
        bool tradeEvolutions, SpeciesNames names, GameData gameData)
    {
        var saveA = Load(sideA, gameData, out var errorA) ?? throw new InvalidOperationException(errorA);
        var saveB = Load(sideB, gameData, out var errorB) ?? throw new InvalidOperationException(errorB);
        if (saveA.Generation != saveB.Generation)
            throw new InvalidOperationException("Getauscht werden kann nur innerhalb einer Generation (Gen 3 ⇄ Gen 3, Gen 4 ⇄ Gen 4).");
        bool sameSave = SaveLocator.Find(sideA.Game, sideA.Profile) == SaveLocator.Find(sideB.Game, sideB.Profile);
        if (sameSave) throw new InvalidOperationException("Beide Seiten nutzen denselben Spielstand – bitte zwei verschiedene wählen.");

        var rawA = saveA.GetRaw(slotA.Box, slotA.Slot);
        var rawB = saveB.GetRaw(slotB.Box, slotB.Slot);

        // Sicherheitsprüfung: sind an den Plätzen noch die ausgewählten Pokémon?
        if (saveA.PidOf(rawA) != slotA.Pokemon.Pid || saveB.PidOf(rawB) != slotB.Pokemon.Pid)
            throw new InvalidOperationException("Die Boxen haben sich inzwischen geändert. Bitte neu auswählen.");

        // Sicherungen
        Backup(sideA);
        Backup(sideB);

        var notes = new List<string>();
        var (newForB, speciesForB) = saveA.Evolve(rawA, tradeEvolutions, names, notes);
        var (newForA, speciesForA) = saveB.Evolve(rawB, tradeEvolutions, names, notes);

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

        var summary = $"{names.German(slotA.Pokemon.Species)} ({sideA.Profile}) ⇄ {names.German(slotB.Pokemon.Species)} ({sideB.Profile})";
        return notes.Count == 0 ? summary : summary + "\n" + string.Join("\n", notes);
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
