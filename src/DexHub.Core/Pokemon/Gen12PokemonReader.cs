using System.Text;
using DexHub.Core.Models;

namespace DexHub.Core.Pokemon;

/// <summary>
/// Liest Team und PC-Boxen aus Spielständen von Gen 1 (Rot/Blau/Gelb) und Gen 2 (Gold/Silber/Kristall).
/// Diese Daten sind nicht verschlüsselt. Offsets für internationale Versionen (auch deutsch),
/// nach Bulbapedia "Save data structure (Generation I/II)".
/// </summary>
public static class Gen12PokemonReader
{
    private sealed record Layout(
        int Generation, int Party, int CurrentBox, int CurrentBoxNumber,
        int BoxCount, int BoxSize, int BoxesPerBank, int MonSize, int PartyMonSize);

    private static readonly Dictionary<SaveFormat, Layout> Layouts = new()
    {
        [SaveFormat.Gen1] = new(1, 0x2F2C, 0x30C0, 0x284C, 12, 0x462, 6, 33, 44),
        [SaveFormat.Gen2GoldSilver] = new(2, 0x288A, 0x2D6C, 0x2724, 14, 0x450, 7, 32, 48),
        [SaveFormat.Gen2Crystal] = new(2, 0x2865, 0x2D10, 0x2700, 14, 0x450, 7, 32, 48),
    };

    public static bool IsSupported(SaveFormat format) => Layouts.ContainsKey(format);

    /// <param name="speciesFromIndex">Gen 1: interne Nummer -> Pokédex-Nummer. Gen 2 nutzt direkt die Pokédex-Nummer.</param>
    public static List<OwnedPokemon> Read(byte[] save, SaveFormat format, string gameTitle, Func<int, int> speciesFromIndex)
    {
        var result = new List<OwnedPokemon>();
        if (!Layouts.TryGetValue(format, out var layout) || !Saves.SaveValidator.IsValidGen12(save, format)) return result;

        // Team: Anzahl, Artenliste (7), Pokémon, Trainernamen (je 11), Spitznamen (je 11)
        ReadList(save, layout.Party, 6, layout.PartyMonSize, layout, gameTitle, "Team", speciesFromIndex, result);

        // Boxen: die aktuelle Box steht im Hauptbereich, die anderen in den Speicherbänken 2 und 3
        int current = save[layout.CurrentBoxNumber] & 0x7F;
        for (int box = 0; box < layout.BoxCount; box++)
        {
            int offset = box == current
                ? layout.CurrentBox
                : (box < layout.BoxesPerBank ? 0x4000 : 0x6000) + (box % layout.BoxesPerBank) * layout.BoxSize;
            ReadList(save, offset, 20, layout.MonSize, layout, gameTitle, $"Box {box + 1}", speciesFromIndex, result);
        }

        Console.WriteLine($"[DexHub] {gameTitle}: {result.Count(p => p.Location == "Team")} im Team, " +
                          $"{result.Count(p => p.Location != "Team")} in Boxen");
        return result;
    }

    private static void ReadList(byte[] save, int offset, int capacity, int monSize, Layout layout,
        string gameTitle, string location, Func<int, int> speciesFromIndex, List<OwnedPokemon> result)
    {
        int count = save[offset];
        if (count == 0 || count > capacity) return; // leer oder (noch) nicht benutzt

        int speciesList = offset + 1;
        int mons = speciesList + capacity + 1;
        int otNames = mons + capacity * monSize;
        int nicknames = otNames + capacity * 11;

        for (int i = 0; i < count; i++)
        {
            int m = mons + i * monSize;
            if (m + monSize > save.Length) break;
            bool isEgg = layout.Generation == 2 && save[speciesList + i] == 0xFD;

            var p = layout.Generation == 1
                ? ParseGen1(save, m, monSize, speciesFromIndex)
                : ParseGen2(save, m, monSize, isEgg);
            if (p is null) continue;

            result.Add(new OwnedPokemon
            {
                Species = p.Value.Species,
                Exp = p.Value.Exp,
                Level = p.Value.Level,
                Pid = 0,
                TrainerId = p.Value.TrainerId,
                Moves = p.Value.Moves,
                Ivs = p.Value.Ivs,
                Evs = p.Value.Evs,
                HeldItem = p.Value.Item,
                Friendship = p.Value.Friendship,
                ShinyOverride = layout.Generation == 2 && p.Value.Shiny,
                IsEgg = isEgg,
                Nickname = DecodeString(save.AsSpan(nicknames + i * 11, 11)),
                TrainerName = DecodeString(save.AsSpan(otNames + i * 11, 11)),
                Generation = layout.Generation,
                GameTitle = gameTitle,
                Location = location,
            });
        }
    }

    private record struct Parsed(int Species, uint Exp, int Level, ushort TrainerId, int[] Moves, int[] Ivs, int[] Evs,
        int Item, int Friendship, bool Shiny);

    private static int U16BE(byte[] d, int o) => d[o] << 8 | d[o + 1];
    private static uint U24BE(byte[] d, int o) => (uint)(d[o] << 16 | d[o + 1] << 8 | d[o + 2]);

    /// <summary>DVs (0–15): Angriff, Verteidigung, Initiative, Spezial in 2 Bytes; KP ergibt sich aus den untersten Bits.</summary>
    private static (int[] Ivs, bool Shiny) Dvs(byte[] d, int o)
    {
        int atk = d[o] >> 4, def = d[o] & 15, spe = d[o + 1] >> 4, spc = d[o + 1] & 15;
        int hp = (atk & 1) << 3 | (def & 1) << 2 | (spe & 1) << 1 | (spc & 1);
        bool shiny = def == 10 && spe == 10 && spc == 10 && (atk & 2) != 0;
        return ([hp, atk, def, spe, spc, spc], shiny);
    }

    private static int[] StatExp(byte[] d, int o)
    {
        int hp = U16BE(d, o), atk = U16BE(d, o + 2), def = U16BE(d, o + 4), spe = U16BE(d, o + 6), spc = U16BE(d, o + 8);
        return [hp, atk, def, spe, spc, spc];
    }

    private static Parsed? ParseGen1(byte[] d, int o, int size, Func<int, int> speciesFromIndex)
    {
        int species = speciesFromIndex(d[o]);
        if (species is < 1 or > 151) return null;
        var (ivs, _) = Dvs(d, o + 0x1B);
        int level = size >= 44 ? d[o + 0x21] : d[o + 0x03];
        return new Parsed(species, U24BE(d, o + 0x0E), level, (ushort)U16BE(d, o + 0x0C),
            [d[o + 0x08], d[o + 0x09], d[o + 0x0A], d[o + 0x0B]], ivs, StatExp(d, o + 0x11), 0, 0, false);
    }

    private static Parsed? ParseGen2(byte[] d, int o, int size, bool isEgg)
    {
        int species = d[o];
        if (species is < 1 or > 251) return null;
        var (ivs, shiny) = Dvs(d, o + 0x15);
        return new Parsed(species, U24BE(d, o + 0x08), d[o + 0x1F], (ushort)U16BE(d, o + 0x06),
            [d[o + 0x02], d[o + 0x03], d[o + 0x04], d[o + 0x05]], ivs, StatExp(d, o + 0x0B), d[o + 0x01],
            isEgg ? 0 : d[o + 0x1B], shiny);
    }

    /// <summary>Zeichensatz von Gen 1/2 (inkl. deutscher Umlaute). 0x50 beendet den Text.</summary>
    public static string DecodeString(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder();
        foreach (var b in data)
        {
            if (b == 0x50 || b == 0x00) break;
            sb.Append(b switch
            {
                >= 0x80 and <= 0x99 => (char)('A' + b - 0x80),
                >= 0xA0 and <= 0xB9 => (char)('a' + b - 0xA0),
                >= 0xF6 => (char)('0' + b - 0xF6),
                0x7F => ' ',
                0xC0 => 'Ä', 0xC1 => 'Ö', 0xC2 => 'Ü', 0xC3 => 'ä', 0xC4 => 'ö', 0xC5 => 'ü',
                0xE3 => '-', 0xE6 => '?', 0xE7 => '!', 0xE8 => '.', 0xEF => '♂', 0xF5 => '♀',
                _ => '?',
            });
        }
        return sb.ToString();
    }
}
