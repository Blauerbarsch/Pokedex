using DexHub.Core.Models;

namespace DexHub.Core.Saves;

/// <summary>Was im Pokédex eines Spielstands eingetragen ist.</summary>
public sealed record DexProgress(IReadOnlySet<int> Caught, IReadOnlySet<int> Seen);

/// <summary>
/// Liest die Pokédex-Flags aus Spielständen.
/// Die Flags sind Bitfelder: Pokémon Nr. n steht in Byte (n-1)/8, Bit (n-1)%8.
/// Offsets gelten für internationale (nicht japanische) Versionen.
/// Quelle: Bulbapedia, "Save data structure (Generation I/II)".
/// </summary>
public static class SaveReader
{
    private sealed record Layout(int CaughtOffset, int SeenOffset, int Count, int MinSize);

    private static readonly Dictionary<SaveFormat, Layout> Layouts = new()
    {
        [SaveFormat.Gen1]           = new(0x25A3, 0x25B6, 151, 0x8000),
        [SaveFormat.Gen2GoldSilver] = new(0x2A4C, 0x2A6C, 251, 0x8000),
        [SaveFormat.Gen2Crystal]    = new(0x2A27, 0x2A47, 251, 0x8000),
    };

    /// <summary>
    /// Gen 4 (DS): Der Spielstand besteht aus zwei Kopien (je 256 KB). Im "allgemeinen Block" am Anfang
    /// jeder Kopie liegt der Pokédex: 4 Byte Kennung 0xBEEFCAFE, dann 64 Byte "gefangen", dann 64 Byte "gesehen".
    /// Offsets nach PKHeX (SAV4DP / SAV4Pt / SAV4HGSS).
    /// </summary>
    private static readonly Dictionary<SaveFormat, int> Gen4DexOffsets = new()
    {
        [SaveFormat.Gen4DiamondPearl] = 0x12DC,
        [SaveFormat.Gen4Platinum] = 0x1328,
        [SaveFormat.Gen4HeartGoldSoulSilver] = 0x12B8,
    };
    private const uint Gen4DexMagic = 0xBEEFCAFE;
    private const int Gen4PartitionSize = 0x40000;
    private const int Gen4SpeciesCount = 493;

    public static bool IsSupported(SaveFormat format) =>
        Layouts.ContainsKey(format) || Gen4DexOffsets.ContainsKey(format);

    /// <summary>Gibt null zurück, wenn das Format nicht unterstützt wird oder die Datei nicht passt.</summary>
    public static DexProgress? Read(string savePath, SaveFormat format)
    {
        if (!IsSupported(format)) return null;

        byte[] data;
        try { data = File.ReadAllBytes(savePath); }
        catch (IOException) { return null; }

        return Parse(data, format);
    }

    /// <summary>Reine Auswertung eines Speicherabbilds – nützlich für Tests.</summary>
    public static DexProgress? Parse(byte[] data, SaveFormat format)
    {
        if (Layouts.TryGetValue(format, out var layout)) return Parse(data, layout);
        if (Gen4DexOffsets.TryGetValue(format, out var dexOffset)) return ParseGen4(data, dexOffset);
        return null;
    }

    private static DexProgress? ParseGen4(byte[] data, int dexOffset)
    {
        // Beide Kopien prüfen. Der Pokédex wird nie kleiner, also ist die Kopie mit
        // mehr Einträgen die neuere – so müssen wir die Prüfsummen nicht auswerten.
        DexProgress? best = null;
        for (int partition = 0; partition + Gen4PartitionSize <= data.Length; partition += Gen4PartitionSize)
        {
            int offset = partition + dexOffset;
            if (offset + 4 + 0x80 > data.Length) continue;
            if (BitConverter.ToUInt32(data, offset) != Gen4DexMagic) continue;

            var progress = new DexProgress(
                ReadFlags(data, offset + 0x04, Gen4SpeciesCount),
                ReadFlags(data, offset + 0x44, Gen4SpeciesCount));

            if (best is null
                || progress.Caught.Count > best.Caught.Count
                || (progress.Caught.Count == best.Caught.Count && progress.Seen.Count > best.Seen.Count))
                best = progress;
        }

        if (best is null)
            Console.WriteLine("[DexHub] Gen-4-Spielstand: Pokédex-Kennung nicht gefunden (Offsets prüfen)");
        return best;
    }

    private static DexProgress? Parse(byte[] data, Layout layout)
    {
        if (data.Length < layout.MinSize) return null;
        return new DexProgress(
            ReadFlags(data, layout.CaughtOffset, layout.Count),
            ReadFlags(data, layout.SeenOffset, layout.Count));
    }

    private static HashSet<int> ReadFlags(byte[] data, int offset, int count)
    {
        var set = new HashSet<int>();
        for (int n = 1; n <= count; n++)
        {
            int index = n - 1;
            if ((data[offset + index / 8] & (1 << (index % 8))) != 0)
                set.Add(n);
        }
        return set;
    }
}
