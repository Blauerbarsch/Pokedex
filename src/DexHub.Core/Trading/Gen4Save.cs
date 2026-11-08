using System.Buffers.Binary;
using DexHub.Core.Models;
using DexHub.Core.Pokemon;

namespace DexHub.Core.Trading;

/// <summary>
/// Gen-4-Spielstand zum Lesen UND Schreiben.
///
/// Aufbau: zwei Kopien (je 256 KB). Jede Kopie hat einen "allgemeinen Block" (Team, Pokédex …) und
/// einen "Box-Block" (PC). Jeder Block endet mit einer Fußzeile: Speicherzähler … CRC16-Prüfsumme.
/// Das Spiel lädt pro Block die gültige Kopie mit dem höheren Zähler.
///
/// Sicherheit: Geschrieben wird nur, wenn wir die vorhandenen Prüfsummen exakt nachrechnen können –
/// dann stimmen Blockgrößen und Positionen sicher.
/// </summary>
public sealed class Gen4Save
{
    private sealed record BlockLayout(int Start, int Size, int FooterSize);
    private sealed record Layout(BlockLayout General, BlockLayout Storage, int BoxOffset, int BoxStride, int DexOffset);

    /// <summary>Nach PKHeX (SAV4DP / SAV4Pt / SAV4HGSS).</summary>
    private static readonly Dictionary<SaveFormat, Layout> Layouts = new()
    {
        [SaveFormat.Gen4DiamondPearl] = new(new(0, 0xC100, 0x14), new(0xC100, 0x121E0, 0x14), 0xC104, 0xFF0, 0x12DC),
        [SaveFormat.Gen4Platinum] = new(new(0, 0xCF2C, 0x14), new(0xCF2C, 0x121E4, 0x14), 0xCF30, 0xFF0, 0x1328),
        [SaveFormat.Gen4HeartGoldSoulSilver] = new(new(0, 0xF628, 0x10), new(0xF700, 0x12310, 0x10), 0xF700, 0x1000, 0x12B8),
    };

    public const int BoxCount = 18;
    public const int SlotsPerBox = 30;
    private const int PokemonSize = 136;
    private const int PartitionSize = 0x40000;

    public byte[] Data { get; }
    public SaveFormat Format { get; }
    public string GameTitle { get; }
    private readonly Layout _layout;

    /// <summary>Neueste gültige Kopie des allgemeinen Blocks bzw. des Box-Blocks (0 oder 0x40000).</summary>
    public int GeneralPartition { get; }
    public int StoragePartition { get; }

    private Gen4Save(byte[] data, SaveFormat format, string gameTitle, Layout layout, int general, int storage)
    {
        Data = data;
        Format = format;
        GameTitle = gameTitle;
        _layout = layout;
        GeneralPartition = general;
        StoragePartition = storage;
    }

    public static bool IsSupported(SaveFormat format) => Layouts.ContainsKey(format);

    /// <summary>Lädt den Spielstand. Gibt null und einen Grund zurück, wenn er nicht sicher bearbeitet werden kann.</summary>
    public static Gen4Save? Load(string path, SaveFormat format, string gameTitle, out string error)
    {
        error = "";
        if (!Layouts.TryGetValue(format, out var layout))
        {
            error = "Dieses Spiel wird von der Tauschzentrale noch nicht unterstützt.";
            return null;
        }

        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (IOException ex) { error = ex.Message; return null; }

        if (data.Length < 2 * PartitionSize)
        {
            error = $"Spielstand ist zu klein ({data.Length} Bytes, erwartet 512 KB).";
            return null;
        }

        int general = NewestValid(data, layout.General);
        int storage = NewestValid(data, layout.Storage);
        if (general < 0 || storage < 0)
        {
            error = "Die Prüfsummen des Spielstands passen nicht zum erwarteten Aufbau – " +
                    "zur Sicherheit wird nichts verändert.";
            Console.WriteLine($"[DexHub] {gameTitle}: Gen-4-Prüfsummen nicht bestätigt (allgemein {general}, Boxen {storage})");
            return null;
        }

        return new Gen4Save(data, format, gameTitle, layout, general, storage);
    }

    // =====================================================================
    // Blöcke, Prüfsummen, Zähler
    // =====================================================================

    /// <summary>CRC-16-CCITT (Startwert 0xFFFF), wie in den Gen-4-Spielen.</summary>
    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }

    private static int ChecksumOffset(int partition, BlockLayout b) => partition + b.Start + b.Size - 2;

    private static bool IsValid(byte[] data, int partition, BlockLayout b)
    {
        int start = partition + b.Start;
        if (start + b.Size > data.Length) return false;
        var body = data.AsSpan(start, b.Size - b.FooterSize);
        if (body.IndexOfAnyExcept((byte)0) < 0) return false; // leerer Block
        ushort stored = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(ChecksumOffset(partition, b)));
        return stored == Crc16(body);
    }

    /// <summary>Speicherzähler aus der Fußzeile (höher = neuer).</summary>
    private static (uint Major, uint Minor) Counter(byte[] data, int partition, BlockLayout b)
    {
        int footer = partition + b.Start + b.Size - b.FooterSize;
        return (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(footer)),
                BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(footer + 4)));
    }

    private static int NewestValid(byte[] data, BlockLayout b)
    {
        bool v0 = IsValid(data, 0, b), v1 = IsValid(data, PartitionSize, b);
        if (v0 && v1)
            return Counter(data, PartitionSize, b).CompareTo(Counter(data, 0, b)) > 0 ? PartitionSize : 0;
        return v0 ? 0 : v1 ? PartitionSize : -1;
    }

    private void FixChecksum(int partition, BlockLayout b)
    {
        int start = partition + b.Start;
        var crc = Crc16(Data.AsSpan(start, b.Size - b.FooterSize));
        BinaryPrimitives.WriteUInt16LittleEndian(Data.AsSpan(ChecksumOffset(partition, b)), crc);
    }

    // =====================================================================
    // Boxen
    // =====================================================================

    private int SlotOffset(int box, int slot) =>
        StoragePartition + _layout.BoxOffset + box * _layout.BoxStride + slot * PokemonSize;

    /// <summary>Verschlüsselte Rohdaten eines Box-Platzes (136 Byte).</summary>
    public byte[] GetRaw(int box, int slot) => Data.AsSpan(SlotOffset(box, slot), PokemonSize).ToArray();

    public void SetRaw(int box, int slot, byte[] raw) => raw.AsSpan(0, PokemonSize).CopyTo(Data.AsSpan(SlotOffset(box, slot)));

    /// <summary>Alle Pokémon in den Boxen mit Position.</summary>
    public List<(int Box, int Slot, OwnedPokemon Pokemon)> BoxPokemon()
    {
        var result = new List<(int, int, OwnedPokemon)>();
        for (int box = 0; box < BoxCount; box++)
            for (int slot = 0; slot < SlotsPerBox; slot++)
            {
                var raw = Data.AsSpan(SlotOffset(box, slot), PokemonSize);
                if (raw.IndexOfAnyExcept((byte)0) < 0) continue;
                var p = Gen4PokemonReader.Parse(raw, GameTitle, $"Box {box + 1}");
                if (p is not null) result.Add((box, slot, p));
            }
        return result;
    }

    /// <summary>Art im Pokédex als gefangen und gesehen eintragen (wie beim Empfangen im Spiel).</summary>
    public void RegisterCaught(int species)
    {
        if (species is < 1 or > 493) return;
        int dex = GeneralPartition + _layout.DexOffset;
        if (BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(dex)) != 0xBEEFCAFE) return;

        int index = species - 1;
        Data[dex + 0x04 + index / 8] |= (byte)(1 << (index % 8)); // gefangen
        Data[dex + 0x44 + index / 8] |= (byte)(1 << (index % 8)); // gesehen
    }

    /// <summary>Prüfsummen neu berechnen und Datei schreiben (erst in Temp-Datei, dann ersetzen).</summary>
    public void Save(string path)
    {
        FixChecksum(GeneralPartition, _layout.General);
        FixChecksum(StoragePartition, _layout.Storage);

        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, Data);
        File.Move(tmp, path, overwrite: true);
    }
}
