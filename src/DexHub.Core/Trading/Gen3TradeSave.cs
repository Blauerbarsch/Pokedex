using System.Buffers.Binary;
using DexHub.Core.Pokedex;
using DexHub.Core.Pokemon;

namespace DexHub.Core.Trading;

/// <summary>
/// Gen-3-Spielstand (Rubin, Saphir, Smaragd, Feuerrot, Blattgrün) zum Lesen UND Schreiben.
///
/// Geschrieben wird in die aktuelle Spielstand-Kopie; danach werden die Prüfsummen aller Sektoren neu
/// berechnet. Sicherheit: Nur wenn beim Laden alle 14 Prüfsummen exakt stimmen, wird überhaupt geschrieben.
/// Nach Bulbapedia "Save data structure (Generation III)" und PKHeX.
/// </summary>
public sealed class Gen3TradeSave : ITradeSave
{
    private const int SectorSize = 0x1000;
    private const int SectorData = 0xF80;
    private const int SlotSize = 14 * SectorSize;
    private const uint Signature = 0x08012025;
    private const int PokemonSize = 80;
    public const int BoxCount = 14, SlotsPerBox = 30;

    /// <summary>Wie viele Bytes je Abschnitt in die Prüfsumme eingehen.</summary>
    private static readonly int[] SectionSizes =
        [0xF2C, 0xF80, 0xF80, 0xF80, 0xF08, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0x7D0];

    // Gen-3-Item-Nummern
    private const int KingsRock = 187, DeepSeaTooth = 192, DeepSeaScale = 193, Everstone = 195,
        MetalCoat = 199, DragonScale = 201, UpGrade = 218;

    private static readonly Dictionary<(int Species, int Item), int> TradeEvolutions = new()
    {
        [(64, 0)] = 65, [(67, 0)] = 68, [(75, 0)] = 76, [(93, 0)] = 94,
        [(61, KingsRock)] = 186, [(79, KingsRock)] = 199,
        [(95, MetalCoat)] = 208, [(123, MetalCoat)] = 212,
        [(117, DragonScale)] = 230, [(137, UpGrade)] = 233,
        [(366, DeepSeaTooth)] = 367, [(366, DeepSeaScale)] = 368,
    };

    public byte[] Data { get; }
    public string GameTitle { get; }
    public int Generation => 3;
    private readonly Dictionary<int, int> _sections; // Abschnitt -> Offset des Sektors
    private readonly GameData _gameData;
    private readonly uint _gameCode;

    private Gen3TradeSave(byte[] data, Dictionary<int, int> sections, string title, GameData gameData)
    {
        Data = data;
        _sections = sections;
        GameTitle = title;
        _gameData = gameData;
        _gameCode = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(sections[0] + 0xAC));
    }

    public static Gen3TradeSave? Load(byte[] data, string title, GameData gameData, out string error)
    {
        error = "";
        if (data.Length < 2 * SlotSize)
        {
            error = $"Spielstand ist zu klein ({data.Length} Bytes, erwartet 128 KB).";
            return null;
        }

        Dictionary<int, int>? best = null;
        uint bestIndex = 0;
        foreach (var slot in new[] { 0, SlotSize })
        {
            var sections = new Dictionary<int, int>();
            uint saveIndex = 0;
            bool valid = true;
            for (int s = 0; s < 14; s++)
            {
                int sector = slot + s * SectorSize;
                if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(sector + 0xFF8)) != Signature) { valid = false; break; }
                int id = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(sector + 0xFF4));
                if (id >= 14) { valid = false; break; }
                if (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(sector + 0xFF6)) != Checksum(data.AsSpan(sector, SectionSizes[id])))
                {
                    valid = false;
                    break;
                }
                sections[id] = sector;
                saveIndex = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(sector + 0xFFC));
            }
            if (!valid || sections.Count != 14) continue;
            if (best is null || saveIndex > bestIndex)
            {
                best = sections;
                bestIndex = saveIndex;
            }
        }

        if (best is null)
        {
            error = "Die Prüfsummen des Spielstands passen nicht zum erwarteten Aufbau – zur Sicherheit wird nichts verändert.";
            Console.WriteLine($"[DexHub] {title}: Gen-3-Prüfsummen nicht bestätigt");
            return null;
        }
        return new Gen3TradeSave(data, best, title, gameData);
    }

    public static ushort Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (int i = 0; i + 4 <= data.Length; i += 4)
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32LittleEndian(data[i..]));
        return (ushort)((sum >> 16) + (sum & 0xFFFF));
    }

    // ---------- PC-Boxen (Abschnitte 5–13 hintereinander) ----------

    /// <summary>Position im zusammengesetzten PC-Puffer -> Position in der Datei.</summary>
    private int PcToFile(int pcOffset) => _sections[5 + pcOffset / SectorData] + pcOffset % SectorData;

    private static int PcOffset(int box, int slot) => 4 + (box * SlotsPerBox + slot) * PokemonSize;

    public byte[] GetRaw(int box, int slot)
    {
        var raw = new byte[PokemonSize];
        int start = PcOffset(box, slot);
        for (int i = 0; i < PokemonSize; i++) raw[i] = Data[PcToFile(start + i)];
        return raw;
    }

    public void SetRaw(int box, int slot, byte[] raw)
    {
        int start = PcOffset(box, slot);
        for (int i = 0; i < PokemonSize; i++) Data[PcToFile(start + i)] = raw[i];
    }

    public List<(int Box, int Slot, OwnedPokemon Pokemon)> BoxPokemon()
    {
        var result = new List<(int, int, OwnedPokemon)>();
        for (int box = 0; box < BoxCount; box++)
            for (int slot = 0; slot < SlotsPerBox; slot++)
            {
                var raw = GetRaw(box, slot);
                if (raw.AsSpan().IndexOfAnyExcept((byte)0) < 0) continue;
                if (Gen3Save.Parse(raw, GameTitle, $"Box {box + 1}", _gameData.SpeciesFromGen3Index) is { } p)
                    result.Add((box, slot, p));
            }
        return result;
    }

    public uint? PidOf(byte[] raw) => Gen3Save.Decrypt(raw) is null ? null : BinaryPrimitives.ReadUInt32LittleEndian(raw);

    // ---------- Pokédex ----------

    /// <summary>Gesehen steht dreimal im Spielstand (Abschnitt 0, 1 und 4) – alle müssen gesetzt werden.</summary>
    public void RegisterCaught(int species)
    {
        if (species is < 1 or > 386) return;
        int index = species - 1;
        byte bit = (byte)(1 << (index % 8));

        int section0 = _sections[0];
        Data[section0 + 0x28 + index / 8] |= bit; // gefangen
        Data[section0 + 0x5C + index / 8] |= bit; // gesehen

        var (seen1, seen4) = _gameCode switch
        {
            0 => (0x938, 0xC0C),  // Rubin/Saphir
            1 => (0x5F8, 0xB98),  // Feuerrot/Blattgrün
            _ => (0x988, 0xCA4),  // Smaragd
        };
        Data[_sections[1] + seen1 + index / 8] |= bit;
        Data[_sections[4] + seen4 + index / 8] |= bit;
    }

    // ---------- Tausch-Entwicklung ----------

    public (byte[] Raw, int Species) Evolve(byte[] raw, bool enabled, SpeciesNames names, List<string> notes)
    {
        if (Gen3Save.Decrypt(raw) is not { } d) return (raw, 0);
        uint pid = BinaryPrimitives.ReadUInt32LittleEndian(raw);
        int growth = Gen3Save.BlockOffset(pid, 'G');
        int misc = Gen3Save.BlockOffset(pid, 'M');

        int species = _gameData.SpeciesFromGen3Index(BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(growth)));
        if (!enabled) return (raw, species);

        int item = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(growth + 2));
        bool isEgg = (BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(misc + 4)) >> 30 & 1) == 1 || (raw[0x13] & 0b100) != 0;
        if (isEgg || item == Everstone) return (raw, species);

        if (!TradeEvolutions.TryGetValue((species, item), out var evolved) &&
            !TradeEvolutions.TryGetValue((species, 0), out evolved))
            return (raw, species);

        int newIndex = _gameData.Gen3IndexFromSpecies(evolved);
        if (newIndex == 0) return (raw, species);

        bool usedItem = item != 0 && TradeEvolutions.ContainsKey((species, item));
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(growth), (ushort)newIndex);
        if (usedItem) BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(growth + 2), 0);

        var result = Gen3Save.Encrypt(raw, d);

        // Ohne Spitznamen steht der Artname (in Großbuchstaben) im Namensfeld – den mitändern
        var nickname = Gen3Save.DecodeString(raw.AsSpan(0x08, 10));
        bool german = raw[0x12] == 5;
        string oldName = (german ? names.German(species) : names.English(species)).ToUpperInvariant();
        if (string.Equals(nickname, oldName, StringComparison.OrdinalIgnoreCase))
        {
            var newName = (german ? names.German(evolved) : names.English(evolved)).ToUpperInvariant();
            if (Gen3Save.EncodeString(newName, 10) is { } encoded) encoded.CopyTo(result, 0x08);
        }

        notes.Add($"✨ {names.German(species)} hat sich beim Tausch zu {names.German(evolved)} entwickelt!");
        return (result, evolved);
    }

    // ---------- Speichern ----------

    public void Save(string path)
    {
        foreach (var (id, sector) in _sections)
        {
            var sum = Checksum(Data.AsSpan(sector, SectionSizes[id]));
            BinaryPrimitives.WriteUInt16LittleEndian(Data.AsSpan(sector + 0xFF6), sum);
        }
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, Data);
        File.Move(tmp, path, overwrite: true);
    }
}
