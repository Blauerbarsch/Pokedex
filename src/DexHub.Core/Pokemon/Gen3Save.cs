using System.Buffers.Binary;
using System.Text;
using DexHub.Core.Saves;

namespace DexHub.Core.Pokemon;

/// <summary>
/// Gen-3-Spielstand (Rubin, Saphir, Smaragd, Feuerrot, Blattgrün), 128 KB.
///
/// Aufbau: zwei Spielstand-Kopien zu je 14 Sektoren à 4 KB. Jeder Sektor hat am Ende eine Kennung
/// (Abschnittsnummer, Prüfsumme, Signatur 0x08012025, Speicherzähler). Die Sektoren sind bei jedem
/// Speichern "rotiert", deshalb sucht man jeden Abschnitt über seine Nummer.
/// Nach Bulbapedia "Save data structure (Generation III)".
/// </summary>
public sealed class Gen3Save
{
    private const int SectorSize = 0x1000;
    private const int SectorData = 0xF80;
    private const int SlotSize = 14 * SectorSize;
    private const uint Signature = 0x08012025;

    private readonly byte[] _data;
    private readonly Dictionary<int, int> _sections; // Abschnittsnummer -> Offset des Sektors

    /// <summary>0 = Rubin/Saphir, 1 = Feuerrot/Blattgrün, sonst Smaragd.</summary>
    public uint GameCode { get; }
    public bool IsFireRedLeafGreen => GameCode == 1;

    private Gen3Save(byte[] data, Dictionary<int, int> sections)
    {
        _data = data;
        _sections = sections;
        GameCode = BinaryPrimitives.ReadUInt32LittleEndian(Section(0)[0xAC..]);
    }

    /// <summary>Lädt die neuere gültige Kopie; null, wenn es kein Gen-3-Spielstand ist.</summary>
    public static Gen3Save? Load(byte[] data)
    {
        if (data.Length < 2 * SlotSize) return null;

        Gen3Save? best = null;
        uint bestIndex = 0;
        foreach (var slot in new[] { 0, SlotSize })
        {
            var sections = new Dictionary<int, int>();
            uint saveIndex = 0;
            for (int s = 0; s < 14; s++)
            {
                int sector = slot + s * SectorSize;
                if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(sector + 0xFF8)) != Signature) continue;
                int id = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(sector + 0xFF4));
                if (id < 14) sections[id] = sector;
                saveIndex = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(sector + 0xFFC));
            }
            if (sections.Count < 14) continue;
            if (best is null || saveIndex > bestIndex)
            {
                best = new Gen3Save(data, sections);
                bestIndex = saveIndex;
            }
        }
        return best;
    }

    private ReadOnlySpan<byte> Section(int id) => _data.AsSpan(_sections[id], SectorData);

    // ---------- Pokédex ----------

    /// <summary>Gefangen bei 0x28, gesehen bei 0x5C im Abschnitt 0 (je 49 Byte, nationale Reihenfolge).</summary>
    public DexProgress ReadDex()
    {
        var section = Section(0);
        return new DexProgress(Flags(section[0x28..], 386), Flags(section[0x5C..], 386));
    }

    private static HashSet<int> Flags(ReadOnlySpan<byte> data, int count)
    {
        var set = new HashSet<int>();
        for (int n = 1; n <= count; n++)
            if ((data[(n - 1) / 8] & (1 << ((n - 1) % 8))) != 0) set.Add(n);
        return set;
    }

    // ---------- Pokémon ----------

    public List<OwnedPokemon> ReadPokemon(string gameTitle, Func<int, int> speciesFromIndex)
    {
        var result = new List<OwnedPokemon>();

        // Team: Abschnitt 1, je 100 Byte (die ersten 80 wie in der Box)
        var team = Section(1);
        int countOffset = IsFireRedLeafGreen ? 0x34 : 0x234;
        int count = Math.Min(6, (int)BinaryPrimitives.ReadUInt32LittleEndian(team[countOffset..]));
        for (int i = 0; i < count; i++)
            if (Parse(team.Slice(countOffset + 4 + i * 100, 80), gameTitle, "Team", speciesFromIndex) is { } p)
                result.Add(p);

        // PC: Abschnitte 5–13 hintereinander, dann 4 Byte "aktuelle Box" und 14 Boxen × 30 Plätze × 80 Byte
        var pc = new List<byte>();
        for (int id = 5; id <= 13; id++) pc.AddRange(Section(id).ToArray());
        var buffer = pc.ToArray();
        for (int box = 0; box < 14; box++)
            for (int slot = 0; slot < 30; slot++)
            {
                int offset = 4 + (box * 30 + slot) * 80;
                if (offset + 80 > buffer.Length) break;
                var raw = buffer.AsSpan(offset, 80);
                if (raw.IndexOfAnyExcept((byte)0) < 0) continue;
                if (Parse(raw, gameTitle, $"Box {box + 1}", speciesFromIndex) is { } p) result.Add(p);
            }

        Console.WriteLine($"[DexHub] {gameTitle}: {result.Count(p => p.Location == "Team")} im Team, " +
                          $"{result.Count(p => p.Location != "Team")} in Boxen");
        return result;
    }

    /// <summary>
    /// Reihenfolge der vier Unterblöcke je nach PID % 24: G = Wachstum, A = Attacken, E = Fleißpunkte, M = Sonstiges.
    /// </summary>
    private static readonly string[] Orders =
    [
        "GAEM", "GAME", "GEAM", "GEMA", "GMAE", "GMEA", "AGEM", "AGME", "AEGM", "AEMG", "AMGE", "AMEG",
        "EGAM", "EGMA", "EAGM", "EAMG", "EMGA", "EMAG", "MGAE", "MGEA", "MAGE", "MAEG", "MEGA", "MEAG",
    ];

    /// <summary>Entschlüsselt ein Pokémon (80 Byte): 48 Byte Daten, XOR mit PID ^ Trainer-ID.</summary>
    private static OwnedPokemon? Parse(ReadOnlySpan<byte> raw, string gameTitle, string location, Func<int, int> speciesFromIndex)
    {
        uint pid = BinaryPrimitives.ReadUInt32LittleEndian(raw);
        uint otId = BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]);
        ushort checksum = BinaryPrimitives.ReadUInt16LittleEndian(raw[0x1C..]);
        uint key = pid ^ otId;

        var data = new byte[48];
        for (int i = 0; i < 48; i += 4)
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i), BinaryPrimitives.ReadUInt32LittleEndian(raw[(0x20 + i)..]) ^ key);

        ushort sum = 0;
        for (int i = 0; i < 48; i += 2) sum = unchecked((ushort)(sum + BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i))));
        if (sum != checksum) return null;

        var order = Orders[pid % 24];
        ReadOnlySpan<byte> Block(char kind) => data.AsSpan(order.IndexOf(kind) * 12, 12);
        var growth = Block('G');
        var attacks = Block('A');
        var evs = Block('E');
        var misc = Block('M');

        int species = speciesFromIndex(BinaryPrimitives.ReadUInt16LittleEndian(growth));
        if (species is < 1 or > 386) return null;

        uint ivWord = BinaryPrimitives.ReadUInt32LittleEndian(misc[4..]);
        ushort origins = BinaryPrimitives.ReadUInt16LittleEndian(misc[2..]);
        bool isEgg = (ivWord >> 30 & 1) == 1 || (raw[0x13] & 0b100) != 0;

        return new OwnedPokemon
        {
            Species = species,
            HeldItem = BinaryPrimitives.ReadUInt16LittleEndian(growth[2..]),
            Exp = BinaryPrimitives.ReadUInt32LittleEndian(growth[4..]),
            Friendship = growth[9],
            Moves = [
                BinaryPrimitives.ReadUInt16LittleEndian(attacks), BinaryPrimitives.ReadUInt16LittleEndian(attacks[2..]),
                BinaryPrimitives.ReadUInt16LittleEndian(attacks[4..]), BinaryPrimitives.ReadUInt16LittleEndian(attacks[6..]),
            ],
            Evs = [evs[0], evs[1], evs[2], evs[3], evs[4], evs[5]],
            Ivs = Enumerable.Range(0, 6).Select(i => (int)(ivWord >> (5 * i) & 31)).ToArray(),
            MetLevel = origins & 0x7F,
            Ball = origins >> 11 & 0xF,
            IsEgg = isEgg,
            Pid = pid,
            TrainerId = (ushort)(otId & 0xFFFF),
            SecretId = (ushort)(otId >> 16),
            Nickname = isEgg ? null : DecodeString(raw.Slice(0x08, 10)),
            TrainerName = DecodeString(raw.Slice(0x14, 7)),
            Generation = 3,
            GameTitle = gameTitle,
            Location = location,
        };
    }

    /// <summary>Zeichensatz von Gen 3 (inkl. deutscher Umlaute). 0xFF beendet den Text.</summary>
    public static string DecodeString(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder();
        foreach (var b in data)
        {
            if (b == 0xFF) break;
            sb.Append(b switch
            {
                >= 0xBB and <= 0xD4 => (char)('A' + b - 0xBB),
                >= 0xD5 and <= 0xEE => (char)('a' + b - 0xD5),
                >= 0xA1 and <= 0xAA => (char)('0' + b - 0xA1),
                0x00 => ' ',
                0xAB => '!', 0xAC => '?', 0xAD => '.', 0xAE => '-', 0xB4 => '\'', 0xB5 => '♂', 0xB6 => '♀',
                0xF1 => 'Ä', 0xF2 => 'Ö', 0xF3 => 'Ü', 0xF4 => 'ä', 0xF5 => 'ö', 0xF6 => 'ü',
                _ => '?',
            });
        }
        return sb.ToString().Trim();
    }
}
