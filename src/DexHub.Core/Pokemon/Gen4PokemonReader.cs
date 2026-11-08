using System.Buffers.Binary;
using System.Text;
using DexHub.Core.Models;

namespace DexHub.Core.Pokemon;

/// <summary>
/// Liest Team und PC-Boxen aus Gen-4-Spielständen (Diamant, Perl, Platin, HeartGold, SoulSilver).
///
/// Jedes Pokémon ist 136 Byte groß ("PK4"). Ab Byte 8 sind die Daten in vier Blöcke (A–D) geteilt,
/// abhängig von der PID durcheinandergewürfelt und mit einem Zufallszahlengenerator (LCRNG)
/// verschlüsselt. Aufbau nach Bulbapedia "Pokémon data structure (Generation IV)" bzw. PKHeX.
/// </summary>
public static class Gen4PokemonReader
{
    private const int PokemonSize = 136;
    private const int PartySlotSize = 236;
    private const int BoxCount = 18;
    private const int SlotsPerBox = 30;
    private const int PartitionSize = 0x40000;
    private const int MaxSpecies = 493;

    private sealed record Layout(int PartyCount, int Party, int Boxes, int BoxStride);

    /// <summary>Positionen innerhalb einer Spielstand-Kopie (nach PKHeX SAV4DP/Pt/HGSS).</summary>
    private static readonly Dictionary<SaveFormat, Layout> Layouts = new()
    {
        [SaveFormat.Gen4DiamondPearl] = new(0x9C, 0xA0, 0xC104, 0xFF0),
        [SaveFormat.Gen4Platinum] = new(0x9C, 0xA0, 0xCF30, 0xFF0),
        [SaveFormat.Gen4HeartGoldSoulSilver] = new(0x94, 0x98, 0xF700, 0x1000),
    };

    public static bool IsSupported(SaveFormat format) => Layouts.ContainsKey(format);

    /// <summary>
    /// Reihenfolge der Blöcke in den gespeicherten Daten, je nach ((PID >> 13) &amp; 31) % 24.
    /// "ABDC" heißt: an erster Stelle steht A, dann B, dann D, dann C.
    /// </summary>
    private static readonly string[] BlockOrders =
    [
        "ABCD", "ABDC", "ACBD", "ACDB", "ADBC", "ADCB",
        "BACD", "BADC", "BCAD", "BCDA", "BDAC", "BDCA",
        "CABD", "CADB", "CBAD", "CBDA", "CDAB", "CDBA",
        "DABC", "DACB", "DBAC", "DBCA", "DCAB", "DCBA",
    ];

    /// <param name="partitionStart">0 oder 0x40000 – welche Spielstand-Kopie die neuere ist (vom Pokédex bestimmt).</param>
    public static List<OwnedPokemon> Read(byte[] save, SaveFormat format, int partitionStart, string gameTitle)
    {
        var result = new List<OwnedPokemon>();
        if (!Layouts.TryGetValue(format, out var layout)) return result;
        if (partitionStart + PartitionSize > save.Length) return result;

        int invalid = 0;

        // Team
        int partyCount = Math.Clamp((int)save[partitionStart + layout.PartyCount], 0, 6);
        for (int i = 0; i < partyCount; i++)
        {
            int offset = partitionStart + layout.Party + i * PartySlotSize;
            var pkm = Parse(save.AsSpan(offset, PokemonSize), gameTitle, "Team");
            if (pkm is not null) result.Add(pkm); else invalid++;
        }

        // PC-Boxen
        for (int box = 0; box < BoxCount; box++)
        {
            for (int slot = 0; slot < SlotsPerBox; slot++)
            {
                int offset = partitionStart + layout.Boxes + box * layout.BoxStride + slot * PokemonSize;
                if (offset + PokemonSize > save.Length) break;
                var raw = save.AsSpan(offset, PokemonSize);
                if (IsEmpty(raw)) continue;

                var pkm = Parse(raw, gameTitle, $"Box {box + 1}");
                if (pkm is not null) result.Add(pkm); else invalid++;
            }
        }

        Console.WriteLine($"[DexHub] {gameTitle}: {result.Count(p => p.Location == "Team")} im Team, " +
                          $"{result.Count(p => p.Location != "Team")} in Boxen" +
                          (invalid > 0 ? $", {invalid} unlesbar" : ""));
        return result;
    }

    private static bool IsEmpty(ReadOnlySpan<byte> raw)
    {
        foreach (var b in raw) if (b != 0) return false;
        return true;
    }

    /// <summary>Entschlüsselt ein Pokémon; null, wenn die Prüfsumme nicht stimmt.</summary>
    private static OwnedPokemon? Parse(ReadOnlySpan<byte> raw, string gameTitle, string location)
    {
        uint pid = BinaryPrimitives.ReadUInt32LittleEndian(raw);
        ushort checksum = BinaryPrimitives.ReadUInt16LittleEndian(raw[6..]);

        // 1. Entschlüsseln: jedes 16-Bit-Wort mit der nächsten Zufallszahl verknüpfen
        var encrypted = new byte[128];
        uint seed = checksum;
        ushort sum = 0;
        for (int i = 0; i < 128; i += 2)
        {
            seed = unchecked(seed * 0x41C64E6D + 0x6073);
            ushort word = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(raw[(8 + i)..]) ^ (seed >> 16));
            BinaryPrimitives.WriteUInt16LittleEndian(encrypted.AsSpan(i), word);
            sum = unchecked((ushort)(sum + word));
        }
        if (sum != checksum) return null;

        // 2. Blöcke in die Reihenfolge A, B, C, D bringen
        var order = BlockOrders[(int)((pid >> 13) & 31) % 24];
        var d = new byte[136]; // Index wie im offiziellen Aufbau (Block A beginnt bei 0x08)
        for (int position = 0; position < 4; position++)
        {
            int block = order[position] - 'A';
            encrypted.AsSpan(position * 32, 32).CopyTo(d.AsSpan(8 + block * 32));
        }

        ushort U16(int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o));
        uint U32(int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));

        int species = U16(0x08);
        if (species is < 1 or > MaxSpecies) return null;

        uint ivWord = U32(0x38);
        bool isEgg = (ivWord >> 30 & 1) == 1;
        bool nicknamed = (ivWord >> 31 & 1) == 1;
        byte flags = d[0x40];

        int metYear = d[0x7B], metMonth = d[0x7C], metDay = d[0x7D];
        DateOnly? metDate = metMonth is >= 1 and <= 12 && metDay is >= 1 and <= 31
            ? SafeDate(2000 + metYear, metMonth, metDay)
            : null;

        int ball = d[0x86] != 0 ? d[0x86] : d[0x83]; // HG/SS speichern neue Bälle extra

        return new OwnedPokemon
        {
            Species = species,
            HeldItem = U16(0x0A),
            TrainerId = U16(0x0C),
            SecretId = U16(0x0E),
            Exp = U32(0x10),
            Friendship = d[0x14],
            Evs = [d[0x18], d[0x19], d[0x1A], d[0x1B], d[0x1C], d[0x1D]],
            Moves = [U16(0x28), U16(0x2A), U16(0x2C), U16(0x2E)],
            Ivs = Enumerable.Range(0, 6).Select(i => (int)(ivWord >> (5 * i) & 31)).ToArray(),
            IsEgg = isEgg,
            Gender = (flags & 0b100) != 0 ? "" : (flags & 0b10) != 0 ? "♀" : "♂",
            Nickname = nicknamed && !isEgg ? DecodeString(d.AsSpan(0x48, 22)) : null,
            TrainerName = DecodeString(d.AsSpan(0x68, 16)),
            MetDate = metDate,
            MetLevel = d[0x84] & 0x7F,
            Ball = ball,
            Pid = pid,
            GameTitle = gameTitle,
            Location = location,
        };
    }

    private static DateOnly? SafeDate(int year, int month, int day)
    {
        try { return new DateOnly(year, month, day); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>
    /// Gen 4 nutzt eine eigene Zeichentabelle mit 16 Bit pro Zeichen.
    /// Hier die wichtigsten Bereiche: Ziffern, Buchstaben, Umlaute/Akzente (wie Latin-1 ab À) und Satzzeichen.
    /// </summary>
    public static string DecodeString(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder();
        for (int i = 0; i + 1 < data.Length; i += 2)
        {
            int c = BinaryPrimitives.ReadUInt16LittleEndian(data[i..]);
            if (c == 0xFFFF || c == 0) break;
            sb.Append(c switch
            {
                >= 0x0121 and <= 0x012A => (char)('0' + c - 0x0121),
                >= 0x012B and <= 0x0144 => (char)('A' + c - 0x012B),
                >= 0x0145 and <= 0x015E => (char)('a' + c - 0x0145),
                >= 0x015F and <= 0x019E => (char)(0xC0 + c - 0x015F), // À … ÿ, enthält ä ö ü ß é
                0x01AB => '!',
                0x01AC => '?',
                0x01AD => ',',
                0x01AE => '.',
                0x01AF => '…',
                0x01B0 => '·',
                0x01B1 => '/',
                0x01B3 => '\'',
                0x01B9 => '(',
                0x01BA => ')',
                0x01BB => '♂',
                0x01BC => '♀',
                0x01BD => '+',
                0x01BE => '-',
                0x01DE => ' ',
                _ => '?',
            });
        }
        return sb.ToString();
    }
}
