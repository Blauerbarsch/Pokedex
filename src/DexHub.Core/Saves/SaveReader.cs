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
        Layouts.ContainsKey(format) || Gen4DexOffsets.ContainsKey(format) || format == SaveFormat.Gen3
        || Trading.Gen5Save.IsSupported(format);

    /// <summary>Gibt null zurück, wenn das Format nicht unterstützt wird oder die Datei nicht passt.</summary>
    public static DexProgress? Read(string savePath, SaveFormat format)
    {
        if (!IsSupported(format)) return null;

        byte[] data;
        try { data = File.ReadAllBytes(savePath); }
        catch (IOException) { return null; }

        return Parse(data, format, Path.GetFileName(savePath));
    }

    /// <summary>Reine Auswertung eines Speicherabbilds – nützlich für Tests.</summary>
    public static DexProgress? Parse(byte[] data, SaveFormat format, string? source = null)
    {
        if (Layouts.TryGetValue(format, out var layout))
        {
            if (!SaveValidator.IsValidGen12(data, format))
            {
                LogOnce($"{source}: kein gültiger Spielstand (noch nie im Spiel gespeichert?)");
                return null;
            }
            return Parse(data, layout);
        }
        if (Gen4DexOffsets.TryGetValue(format, out var dexOffset)) return ParseGen4(data, dexOffset, source ?? "?");
        if (Trading.Gen5Save.IsSupported(format))
        {
            var gen5 = Trading.Gen5Save.Load(data, format, source ?? "", out var error);
            if (gen5 is null) LogOnce($"{source}: {error}");
            return gen5?.ReadDex();
        }
        if (format == SaveFormat.Gen3)
        {
            var gen3 = Pokemon.Gen3Save.Load(data);
            if (gen3 is null) LogOnce($"{source}: kein gültiger Gen-3-Spielstand ({data.Length} Bytes)");
            return gen3?.ReadDex();
        }
        return null;
    }

    /// <summary>Damit dieselbe Meldung nicht bei jeder Aktualisierung erneut im Terminal landet.</summary>
    private static readonly HashSet<string> LoggedOnce = [];

    private static void LogOnce(string message)
    {
        lock (LoggedOnce)
            if (LoggedOnce.Add(message))
                Console.WriteLine("[DexHub] " + message);
    }

    /// <summary>Welche der beiden Gen-4-Spielstand-Kopien die neuere ist (0 oder 0x40000), -1 wenn keine gültig.</summary>
    public static int FindNewestGen4Partition(byte[] data, SaveFormat format)
    {
        if (!Gen4DexOffsets.TryGetValue(format, out var dexOffset)) return -1;
        int best = -1, bestCaught = -1, bestSeen = -1;
        for (int partition = 0; partition < data.Length; partition += Gen4PartitionSize)
        {
            int offset = FindGen4Dex(data, partition, dexOffset);
            if (offset < 0) continue;
            int caught = ReadFlags(data, offset + 0x04, Gen4SpeciesCount).Count;
            int seen = ReadFlags(data, offset + 0x44, Gen4SpeciesCount).Count;
            if (caught > bestCaught || (caught == bestCaught && seen > bestSeen))
                (best, bestCaught, bestSeen) = (partition, caught, seen);
        }
        return best;
    }

    private static DexProgress? ParseGen4(byte[] data, int dexOffset, string source)
    {
        // Beide Kopien prüfen. Der Pokédex wird nie kleiner, also ist die Kopie mit
        // mehr Einträgen die neuere – so müssen wir die Prüfsummen nicht auswerten.
        DexProgress? best = null;
        var found = new List<string>();

        for (int partition = 0; partition < data.Length; partition += Gen4PartitionSize)
        {
            int offset = FindGen4Dex(data, partition, dexOffset);
            if (offset < 0) continue;
            found.Add($"0x{offset:X}");

            var progress = new DexProgress(
                ReadFlags(data, offset + 0x04, Gen4SpeciesCount),
                ReadFlags(data, offset + 0x44, Gen4SpeciesCount));

            if (best is null
                || progress.Caught.Count > best.Caught.Count
                || (progress.Caught.Count == best.Caught.Count && progress.Seen.Count > best.Seen.Count))
                best = progress;
        }

        LogOnce(best is null
            ? $"{source}: kein Gen-4-Pokédex gefunden ({data.Length} Bytes) – ist das ein Spielstand dieses Spiels?"
            : $"{source}: Pokédex bei {string.Join(", ", found)} – gefangen: {Ids(best.Caught)} – gesehen: {Ids(best.Seen)}");
        return best;
    }

    /// <summary>
    /// Sucht die Pokédex-Kennung: zuerst an der bekannten Stelle, sonst im allgemeinen Block der Kopie.
    /// Gibt -1 zurück, wenn nichts gefunden wurde.
    /// </summary>
    private static int FindGen4Dex(byte[] data, int partitionStart, int knownOffset)
    {
        int known = partitionStart + knownOffset;
        if (IsGen4Dex(data, known)) return known;

        int searchEnd = Math.Min(data.Length, partitionStart + 0x10000); // allgemeiner Block ist < 64 KB
        for (int o = partitionStart; o < searchEnd; o += 4)
            if (IsGen4Dex(data, o)) return o;
        return -1;
    }

    /// <summary>Kurze Liste der Nummern für die Diagnose, z. B. "3 (#387, #390, #393)".</summary>
    private static string Ids(IReadOnlySet<int> ids) =>
        ids.Count == 0 ? "0" : $"{ids.Count} (" + string.Join(", ", ids.Order().Take(12).Select(i => "#" + i)) + (ids.Count > 12 ? ", …)" : ")");

    private static bool IsGen4Dex(byte[] data, int offset) =>
        offset >= 0 && offset + 0x84 <= data.Length && BitConverter.ToUInt32(data, offset) == Gen4DexMagic;

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
