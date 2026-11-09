using System.Buffers.Binary;
using DexHub.Core.Models;
using DexHub.Core.Pokemon;
using DexHub.Core.Saves;

namespace DexHub.Core.Trading;

/// <summary>
/// Gen-5-Spielstand (Schwarz/Weiß, Schwarz 2/Weiß 2), 512 KB.
///
/// Aufbau (nach PKHeX, SaveBlockAccessor5BW/B2W2): Die Daten liegen in Blöcken ab Offset 0. Jeder Block hat
/// eine CRC16-Prüfsumme direkt hinter seinen Daten und eine Kopie davon in einer Prüfsummen-Tabelle
/// (selbst ein Block mit eigener Prüfsumme). Boxen: 24 Blöcke ab 0x400 (je 30 × 136 Byte, Abstand 0x1000).
///
/// Sicherheit: Geschrieben wird nur, wenn Pokédex-, Box- und Tabellen-Prüfsummen beim Laden stimmen.
/// </summary>
public sealed class Gen5Save : ITradeSave
{
    private sealed record Block(int Offset, int Length, int Checksum, int Mirror);

    private sealed record Layout(int ChecksumTable, int ChecksumTableLength, int DexOffset, int DexLength, int DexIndex);

    private static readonly Dictionary<SaveFormat, Layout> Layouts = new()
    {
        [SaveFormat.Gen5BlackWhite] = new(0x23F00, 0x8C, 0x21600, 0x4D4, 55),
        [SaveFormat.Gen5Black2White2] = new(0x25F00, 0x94, 0x21400, 0x4DC, 54),
    };

    public const int BoxCount = 24, SlotsPerBox = 30;
    private const int PokemonSize = 136;
    private const int BoxStart = 0x400, BoxStride = 0x1000, BoxLength = 0xFF0;
    private const int Party = 0x18E00, PartySlotSize = 220;
    private const int SpeciesCount = 649, FlagBytes = 0x54;

    // Gen-5-Item-Nummern (wie Gen 4)
    private const int KingsRock = 221, DeepSeaTooth = 226, DeepSeaScale = 227, MetalCoat = 233, DragonScale = 235,
        UpGrade = 252, Protector = 321, Electirizer = 322, Magmarizer = 323, DubiousDisc = 324, ReaperCloth = 325, Everstone = 229;
    private const int Karrablast = 588, Shelmet = 616;

    private static readonly Dictionary<(int Species, int Item), int> TradeEvolutions = new()
    {
        [(64, 0)] = 65, [(67, 0)] = 68, [(75, 0)] = 76, [(93, 0)] = 94,
        [(525, 0)] = 526, [(533, 0)] = 534, // Kiesling -> Gigalith, Strepoli -> Meistagrif
        [(61, KingsRock)] = 186, [(79, KingsRock)] = 199, [(95, MetalCoat)] = 208, [(123, MetalCoat)] = 212,
        [(117, DragonScale)] = 230, [(137, UpGrade)] = 233, [(366, DeepSeaTooth)] = 367, [(366, DeepSeaScale)] = 368,
        [(112, Protector)] = 464, [(125, Electirizer)] = 466, [(126, Magmarizer)] = 467,
        [(233, DubiousDisc)] = 474, [(356, ReaperCloth)] = 477,
    };

    public byte[] Data { get; }
    public string GameTitle { get; }
    public int Generation => 5;
    private readonly Layout _layout;

    /// <summary>Für Laming/Schnuthelm: die Art des Tauschpartners (wird vor dem Tausch gesetzt).</summary>
    public int PartnerSpecies { get; set; }

    private Gen5Save(byte[] data, Layout layout, string title)
    {
        Data = data;
        _layout = layout;
        GameTitle = title;
    }

    public static bool IsSupported(SaveFormat format) => Layouts.ContainsKey(format);

    public static Gen5Save? Load(byte[] data, SaveFormat format, string title, out string error)
    {
        error = "";
        if (!Layouts.TryGetValue(format, out var layout))
        {
            error = "Dieses Spiel wird noch nicht unterstützt.";
            return null;
        }
        if (data.Length < 0x80000)
        {
            error = $"Spielstand ist zu klein ({data.Length} Bytes, erwartet 512 KB).";
            return null;
        }

        var save = new Gen5Save(data, layout, title);
        if (!save.IsValid(save.ChecksumBlock()) || !save.IsValid(save.DexBlock()))
        {
            error = "Die Prüfsummen des Spielstands passen nicht zum erwarteten Aufbau – " +
                    "noch nie im Spiel gespeichert? Zur Sicherheit wird nichts verändert.";
            return null;
        }
        return save;
    }

    // ---------- Blöcke und Prüfsummen ----------

    private Block ChecksumBlock() =>
        new(_layout.ChecksumTable, _layout.ChecksumTableLength, _layout.ChecksumTable + _layout.ChecksumTableLength + 0xE, -1);

    private Block DexBlock() =>
        new(_layout.DexOffset, _layout.DexLength, _layout.DexOffset + _layout.DexLength + 2, _layout.ChecksumTable + _layout.DexIndex * 2);

    private Block BoxBlock(int box)
    {
        int offset = BoxStart + box * BoxStride;
        return new(offset, BoxLength, offset + BoxLength + 2, _layout.ChecksumTable + (box + 1) * 2);
    }

    private bool IsValid(Block b) =>
        BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(b.Checksum)) == Gen4Save.Crc16(Data.AsSpan(b.Offset, b.Length));

    private void Fix(Block b)
    {
        var crc = Gen4Save.Crc16(Data.AsSpan(b.Offset, b.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(Data.AsSpan(b.Checksum), crc);
        if (b.Mirror >= 0) BinaryPrimitives.WriteUInt16LittleEndian(Data.AsSpan(b.Mirror), crc);
    }

    // ---------- Pokédex ----------

    /// <summary>Gefangen ab +0x08, gesehen in vier Bereichen (♂, ♀, schillernd ♂, schillernd ♀) ab +0x5C.</summary>
    public DexProgress ReadDex()
    {
        int dex = _layout.DexOffset;
        var caught = Flags(dex + 0x08);
        var seen = new HashSet<int>(caught);
        for (int region = 0; region < 4; region++) seen.UnionWith(Flags(dex + 0x5C + region * FlagBytes));
        return new DexProgress(caught, seen);
    }

    private HashSet<int> Flags(int offset)
    {
        var set = new HashSet<int>();
        for (int n = 1; n <= SpeciesCount; n++)
            if ((Data[offset + (n - 1) / 8] & (1 << ((n - 1) % 8))) != 0) set.Add(n);
        return set;
    }

    public void RegisterCaught(int species)
    {
        if (species is < 1 or > SpeciesCount) return;
        int index = species - 1, dex = _layout.DexOffset;
        byte bit = (byte)(1 << (index % 8));
        Data[dex + 0x08 + index / 8] |= bit;
        bool seen = false;
        for (int region = 0; region < 4; region++) seen |= (Data[dex + 0x5C + region * FlagBytes + index / 8] & bit) != 0;
        if (!seen) Data[dex + 0x5C + index / 8] |= bit; // als ♂/geschlechtslos gesehen
        _dexChanged = true;
    }

    private bool _dexChanged;
    private readonly HashSet<int> _changedBoxes = [];

    // ---------- Pokémon ----------

    public List<OwnedPokemon> ReadPokemon(string title)
    {
        var result = new List<OwnedPokemon>();
        int count = Math.Clamp((int)Data[Party + 4], 0, 6);
        for (int i = 0; i < count; i++)
            if (Gen5PokemonReader.Parse(Data.AsSpan(Party + 8 + i * PartySlotSize, PokemonSize), title, "Team") is { } p)
                result.Add(p);
        result.AddRange(BoxPokemon().Select(b => b.Pokemon));
        return result;
    }

    private int SlotOffset(int box, int slot) => BoxStart + box * BoxStride + slot * PokemonSize;

    public List<(int Box, int Slot, OwnedPokemon Pokemon)> BoxPokemon()
    {
        var result = new List<(int, int, OwnedPokemon)>();
        for (int box = 0; box < BoxCount; box++)
            for (int slot = 0; slot < SlotsPerBox; slot++)
            {
                var raw = Data.AsSpan(SlotOffset(box, slot), PokemonSize);
                if (raw.IndexOfAnyExcept((byte)0) < 0) continue;
                if (Gen5PokemonReader.Parse(raw, GameTitle, $"Box {box + 1}") is { } p) result.Add((box, slot, p));
            }
        return result;
    }

    public byte[] GetRaw(int box, int slot) => Data.AsSpan(SlotOffset(box, slot), PokemonSize).ToArray();

    public void SetRaw(int box, int slot, byte[] raw)
    {
        raw.AsSpan(0, PokemonSize).CopyTo(Data.AsSpan(SlotOffset(box, slot)));
        _changedBoxes.Add(box);
    }

    public (int Box, int Slot)? FirstFreeSlot()
    {
        for (int box = 0; box < BoxCount; box++)
            for (int slot = 0; slot < SlotsPerBox; slot++)
            {
                var raw = Data.AsSpan(SlotOffset(box, slot), PokemonSize);
                if (raw.IndexOfAnyExcept((byte)0) < 0) return (box, slot);
                if (Gen4PokemonReader.Decrypt(raw) is { } d && BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(0x08)) == 0)
                    return (box, slot);
            }
        return null;
    }

    public void Clear(int box, int slot) => SetRaw(box, slot, Gen4PokemonReader.Encrypt(new byte[PokemonSize]));

    public uint? PidOf(byte[] raw) => Gen5PokemonReader.Parse(raw, "", "")?.Pid;

    public (byte[] Raw, int Species) Evolve(byte[] raw, bool enabled, SpeciesNames names, List<string> notes)
    {
        var d = Gen4PokemonReader.Decrypt(raw);
        if (d is null) return (raw, 0);
        int species = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(0x08));
        if (!enabled) return (raw, species);

        int item = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(0x0A));
        bool isEgg = (BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(0x38)) >> 30 & 1) == 1;
        if (isEgg || item == Everstone) return (raw, species);

        int evolved;
        bool usedItem = false;
        if (species == Karrablast && PartnerSpecies == Shelmet) evolved = 589;      // Laming -> Cavalanzas
        else if (species == Shelmet && PartnerSpecies == Karrablast) evolved = 617; // Schnuthelm -> Hydragil
        else if (item != 0 && TradeEvolutions.TryGetValue((species, item), out evolved)) usedItem = true;
        else if (!TradeEvolutions.TryGetValue((species, 0), out evolved)) return (raw, species);

        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(0x08), (ushort)evolved);
        if (usedItem) BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(0x0A), 0);

        bool nicknamed = (BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(0x38)) >> 31 & 1) == 1;
        if (!nicknamed)
        {
            bool german = d[0x17] == 5;
            var name = german ? names.German(evolved) : names.English(evolved);
            Gen5PokemonReader.EncodeString(name.ToUpperInvariant(), 22).CopyTo(d.AsSpan(0x48));
        }

        notes.Add($"✨ {names.German(species)} hat sich beim Tausch zu {names.German(evolved)} entwickelt!");
        return (Gen4PokemonReader.Encrypt(d), evolved);
    }

    // ---------- Speichern ----------

    public void Save(string path)
    {
        foreach (var box in _changedBoxes) Fix(BoxBlock(box));
        if (_dexChanged) Fix(DexBlock());
        Fix(ChecksumBlock()); // die Tabelle hat sich geändert -> ihre eigene Prüfsumme auch

        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, Data);
        File.Move(tmp, path, overwrite: true);
    }
}
