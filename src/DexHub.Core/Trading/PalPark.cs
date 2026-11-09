using System.Buffers.Binary;
using DexHub.Core.Pokemon;

namespace DexHub.Core.Trading;

/// <summary>Was der Pal-Park über eine Art wissen muss.</summary>
/// <param name="Abilities">Fähigkeiten-Nummern von Platz 1 und 2 (Platz 2 = 0, wenn es keine zweite gibt).</param>
/// <param name="GenderRate">-1 geschlechtslos, sonst Achtel weiblich (0 = nur männlich, 8 = nur weiblich).</param>
public sealed record SpeciesInfo(int Ability1, int Ability2, int GenderRate);

/// <summary>
/// Wandelt ein Gen-3-Pokémon (80 Byte) in ein Gen-4-Pokémon (136 Byte) um – wie der Pal-Park in
/// Diamant/Perl/Platin/HeartGold/SoulSilver. Nach Bulbapedia und PKHeX (PK3 → PK4).
///
/// Übernommen: PID, Art, Trainer, EP, Attacken, AP-Plus, IS-, FP- und Wettbewerbswerte, Ball, Herkunftsspiel,
/// Sprache, Pokérus, Spitzname. Gesetzt wie im Pal-Park: Fundort "Pal-Park", Fundlevel = aktuelles Level,
/// Freundschaft 70. Getragene Items und Bänder bleiben zurück.
/// </summary>
public static class PalPark
{
    private const int PalParkLocation = 0x37;
    private const int Unown = 201;

    public static byte[] Convert(byte[] pk3, Func<int, int> speciesFromIndex, Func<int, SpeciesInfo?> speciesInfo,
        Func<int, uint, int> levelFromExp, SpeciesNames names, List<string> notes)
    {
        var data = Gen3Save.Decrypt(pk3) ?? throw new InvalidOperationException("Die Pokémon-Daten sind beschädigt.");
        uint pid = BinaryPrimitives.ReadUInt32LittleEndian(pk3);
        uint otId = BinaryPrimitives.ReadUInt32LittleEndian(pk3.AsSpan(4));

        var growth = data.AsSpan(Gen3Save.BlockOffset(pid, 'G'), 12);
        var attacks = data.AsSpan(Gen3Save.BlockOffset(pid, 'A'), 12);
        var evs = data.AsSpan(Gen3Save.BlockOffset(pid, 'E'), 12);
        var misc = data.AsSpan(Gen3Save.BlockOffset(pid, 'M'), 12);

        int species = speciesFromIndex(BinaryPrimitives.ReadUInt16LittleEndian(growth));
        if (species is < 1 or > 386) throw new InvalidOperationException("Unbekannte Pokémon-Art.");
        uint ivWord = BinaryPrimitives.ReadUInt32LittleEndian(misc[4..]);
        if ((ivWord >> 30 & 1) == 1 || (pk3[0x13] & 0b100) != 0)
            throw new InvalidOperationException("Eier können nicht in Gen 4 übertragen werden – erst ausbrüten lassen.");
        var info = speciesInfo(species) ?? throw new InvalidOperationException("Für diese Art fehlen Daten (Fähigkeiten).");

        uint exp = BinaryPrimitives.ReadUInt32LittleEndian(growth[4..]);
        ushort origins = BinaryPrimitives.ReadUInt16LittleEndian(misc[2..]);
        byte language = pk3[0x12];
        bool german = language == 5;

        var d = new byte[136];
        BinaryPrimitives.WriteUInt32LittleEndian(d, pid);

        // Block A
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(0x08), (ushort)species);
        if (BinaryPrimitives.ReadUInt16LittleEndian(growth[2..]) != 0)
            notes.Add("Das getragene Item bleibt zurück (wird in Gen 4 anders nummeriert).");
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(0x0C), otId); // TID + SID
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(0x10), exp);
        d[0x14] = 70; // Freundschaft wie im Pal-Park
        bool secondAbility = (ivWord >> 31 & 1) == 1 && info.Ability2 != 0;
        d[0x15] = (byte)(secondAbility ? info.Ability2 : info.Ability1);
        d[0x17] = language;
        evs[..6].CopyTo(d.AsSpan(0x18));       // FP: KP, Angr., Vert., Init., Sp.-Angr., Sp.-Vert.
        evs.Slice(6, 6).CopyTo(d.AsSpan(0x1E)); // Wettbewerbswerte

        // Block B
        attacks[..8].CopyTo(d.AsSpan(0x28));    // vier Attacken
        attacks.Slice(8, 4).CopyTo(d.AsSpan(0x30)); // AP
        byte ppUps = growth[8];
        for (int i = 0; i < 4; i++) d[0x34 + i] = (byte)(ppUps >> (2 * i) & 3);

        var nickname = Gen3Save.DecodeString(pk3.AsSpan(0x08, 10));
        string speciesName = (german ? names.German(species) : names.English(species)).ToUpperInvariant();
        bool nicknamed = nickname.Length > 0 && !string.Equals(nickname, speciesName, StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(nickname, names.English(species), StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(nickname, names.German(species), StringComparison.OrdinalIgnoreCase);
        uint ivs = ivWord & 0x3FFF_FFFF;
        if (nicknamed) ivs |= 1u << 31;
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(0x38), ivs);

        int form = species == Unown ? UnownForm(pid) : 0;
        d[0x40] = (byte)(Gender(pid, info.GenderRate) | form << 3);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(0x46), PalParkLocation); // Fundort (Platin/HGSS)

        // Block C
        Gen4PokemonReader.EncodeString(nicknamed ? nickname : speciesName, 22).CopyTo(d.AsSpan(0x48));
        d[0x5F] = (byte)(origins >> 7 & 0xF); // Herkunftsspiel (Rubin, Saphir, … gleiche Nummern)

        // Block D
        Gen4PokemonReader.EncodeString(Gen3Save.DecodeString(pk3.AsSpan(0x14, 7)), 16).CopyTo(d.AsSpan(0x68));
        var today = DateTime.Today;
        d[0x7B] = (byte)(today.Year - 2000);
        d[0x7C] = (byte)today.Month;
        d[0x7D] = (byte)today.Day;
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(0x80), PalParkLocation); // Fundort (Diamant/Perl)
        d[0x82] = misc[0];                                     // Pokérus
        d[0x83] = (byte)(origins >> 11 & 0xF);                 // Ball
        int level = Math.Clamp(levelFromExp(species, exp), 1, 100);
        d[0x84] = (byte)(level | (origins >> 15 & 1) << 7);    // Fundlevel + Geschlecht des Trainers

        return Gen4PokemonReader.Encrypt(d);
    }

    /// <summary>Geschlechts-Bits für Gen 4: 0 männlich, 2 weiblich, 4 geschlechtslos.</summary>
    private static int Gender(uint pid, int genderRate) => genderRate switch
    {
        < 0 => 0b100,
        0 => 0,
        8 => 0b10,
        _ => (pid & 0xFF) < Threshold(genderRate) ? 0b10 : 0,
    };

    private static int Threshold(int genderRate) => genderRate switch
    {
        1 => 31, 2 => 63, 4 => 127, 6 => 191, 7 => 225, _ => genderRate * 32,
    };

    /// <summary>Icognito-Buchstabe aus der PID (Gen 3).</summary>
    private static int UnownForm(uint pid) =>
        (int)(((pid >> 24 & 3) << 6 | (pid >> 16 & 3) << 4 | (pid >> 8 & 3) << 2 | (pid & 3)) % 28);
}
