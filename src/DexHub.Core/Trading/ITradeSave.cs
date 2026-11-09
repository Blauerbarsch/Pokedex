using DexHub.Core.Pokemon;

namespace DexHub.Core.Trading;

/// <summary>Ein Spielstand, in dessen PC-Boxen getauscht werden kann (Gen 3 oder Gen 4).</summary>
public interface ITradeSave
{
    string GameTitle { get; }
    int Generation { get; }

    /// <summary>Alle Pokémon in den Boxen mit Position.</summary>
    List<(int Box, int Slot, OwnedPokemon Pokemon)> BoxPokemon();

    /// <summary>Verschlüsselte Rohdaten eines Box-Platzes.</summary>
    byte[] GetRaw(int box, int slot);
    void SetRaw(int box, int slot, byte[] raw);

    /// <summary>Erster freier Box-Platz oder null, wenn alle Boxen voll sind.</summary>
    (int Box, int Slot)? FirstFreeSlot();

    /// <summary>Platz leeren (so, wie das Spiel einen leeren Platz speichert).</summary>
    void Clear(int box, int slot);

    /// <summary>PID der Rohdaten, null wenn sie nicht gültig sind.</summary>
    uint? PidOf(byte[] raw);

    /// <summary>Tausch-Entwicklung anwenden. Gibt neue Rohdaten und (neue) Pokédex-Nummer zurück.</summary>
    (byte[] Raw, int Species) Evolve(byte[] raw, bool enabled, SpeciesNames names, List<string> notes);

    /// <summary>Art im Pokédex als gefangen und gesehen eintragen.</summary>
    void RegisterCaught(int species);

    /// <summary>Prüfsummen neu berechnen und Datei schreiben.</summary>
    void Save(string path);
}

/// <summary>Artnamen auf Deutsch und Englisch (für Namensfelder ohne Spitznamen).</summary>
public sealed record SpeciesNames(Func<int, string> German, Func<int, string> English);
