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

    public static bool IsSupported(SaveFormat format) => Layouts.ContainsKey(format);

    /// <summary>Gibt null zurück, wenn das Format nicht unterstützt wird oder die Datei nicht passt.</summary>
    public static DexProgress? Read(string savePath, SaveFormat format)
    {
        if (!Layouts.TryGetValue(format, out var layout)) return null;

        byte[] data;
        try { data = File.ReadAllBytes(savePath); }
        catch (IOException) { return null; }

        return Parse(data, layout);
    }

    /// <summary>Reine Auswertung eines Speicherabbilds – nützlich für Tests.</summary>
    public static DexProgress? Parse(byte[] data, SaveFormat format) =>
        Layouts.TryGetValue(format, out var layout) ? Parse(data, layout) : null;

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
