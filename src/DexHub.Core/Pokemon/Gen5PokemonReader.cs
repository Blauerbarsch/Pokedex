using System.Buffers.Binary;
using System.Text;

namespace DexHub.Core.Pokemon;

/// <summary>
/// Pokémon aus Gen 5 (Schwarz/Weiß 1 und 2). Gleiche Verschlüsselung und Blockreihenfolge wie Gen 4,
/// aber Texte in UTF-16, Wesen extra gespeichert und 649 Arten. Nach PKHeX (PK5).
/// </summary>
public static class Gen5PokemonReader
{
    public const int PokemonSize = 136;
    public const int MaxSpecies = 649;

    public static OwnedPokemon? Parse(ReadOnlySpan<byte> raw, string gameTitle, string location)
    {
        if (raw.Length < PokemonSize) return null;
        var d = Gen4PokemonReader.Decrypt(raw[..PokemonSize]);
        if (d is null) return null;

        ushort U16(int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o));
        uint U32(int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));

        int species = U16(0x08);
        if (species is < 1 or > MaxSpecies) return null;

        uint ivWord = U32(0x38);
        bool isEgg = (ivWord >> 30 & 1) == 1;
        bool nicknamed = (ivWord >> 31 & 1) == 1;
        byte flags = d[0x40];

        DateOnly? metDate = null;
        if (d[0x7C] is >= 1 and <= 12 && d[0x7D] is >= 1 and <= 31)
            try { metDate = new DateOnly(2000 + d[0x7B], d[0x7C], d[0x7D]); } catch (ArgumentOutOfRangeException) { }

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
            Ball = d[0x83],
            Pid = BinaryPrimitives.ReadUInt32LittleEndian(d),
            NatureOverride = d[0x41] < 25 ? d[0x41] : null,
            Generation = 5,
            GameTitle = gameTitle,
            Location = location,
        };
    }

    /// <summary>UTF-16, Ende bei 0xFFFF. Die Spiele nutzen eigene Zeichen für ♂/♀.</summary>
    public static string DecodeString(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder();
        for (int i = 0; i + 1 < data.Length; i += 2)
        {
            var c = (char)BinaryPrimitives.ReadUInt16LittleEndian(data[i..]);
            if (c is (char)0xFFFF or (char)0) break;
            sb.Append(c switch { '⑭' => '♂', '⑮' => '♀', _ => c });
        }
        return sb.ToString();
    }

    public static byte[] EncodeString(string text, int maxBytes)
    {
        var result = new byte[maxBytes];
        int pos = 0;
        foreach (var ch in text)
        {
            if (pos + 4 > maxBytes) break; // Platz für das Endezeichen
            var c = ch switch { '♂' => '⑭', '♀' => '⑮', _ => ch };
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(pos), c);
            pos += 2;
        }
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(pos), 0xFFFF);
        return result;
    }
}
