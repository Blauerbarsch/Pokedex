using System.Buffers.Binary;
using DexHub.Core.Libretro;
using DexHub.Core.Models;
using DexHub.Core.Pokemon;
using DexHub.Core.Saves;

namespace DexHub.Core.Live;

/// <summary>
/// Liest den Pokédex direkt aus dem Arbeitsspeicher des laufenden Spiels – so erscheinen gefangene Pokémon
/// sofort, nicht erst nach dem Speichern im Spiel.
///
/// Wie der Pokédex im Speicher gefunden wird:
/// • Gen 1/2: feste Adressen (internationale Versionen).
/// • Gen 3: Spielername + Trainer-ID aus dem Spielstand im Speicher suchen (die Spiele verschieben ihre Daten).
/// • Gen 4: Kennung 0xBEEFCAFE des Pokédex im Hauptspeicher suchen.
/// • Gen 5: Kennung des Pokédex aus dem Spielstand im Hauptspeicher suchen.
///
/// Zur Sicherheit wird jedes Ergebnis geprüft: plausible Anzahlen, gefangen ⊆ gesehen, und alles, was im
/// Spielstand schon gefangen ist, muss auch im Speicher stehen (der Pokédex wird nie kleiner).
/// Sonst gibt es kein Ergebnis – lieber nichts anzeigen als etwas Falsches.
/// </summary>
public sealed class LiveDexReader(GameEntry game)
{
    private readonly SaveFormat _format = game.Info.SaveFormat;
    private int _cachedOffset = -1;

    public static bool IsSupported(SaveFormat format) => format != SaveFormat.None;

    private int MaxSpecies => game.Info.Generation switch { 1 => 151, 2 => 251, 3 => 386, 4 => 493, _ => 649 };

    public DexProgress? Read(LibretroCore core)
    {
        try
        {
            var sram = core.SaveRam.ToArray();
            var saved = sram.Length > 0 ? SaveReader.Parse(sram, _format) : null;
            var live = _format switch
            {
                SaveFormat.Gen1 => ReadGb(core, game.Info.Version == 3 ? (0xD2F6, 0xD309) : (0xD2F7, 0xD30A)),
                SaveFormat.Gen2GoldSilver => ReadGb(core, (0xDBE4, 0xDC04)),
                SaveFormat.Gen2Crystal => ReadGb(core, (0xDE99, 0xDEB9)),
                SaveFormat.Gen3 => ReadGen3(core, sram),
                SaveFormat.Gen4DiamondPearl or SaveFormat.Gen4Platinum or SaveFormat.Gen4HeartGoldSoulSilver => ReadGen4(core, saved),
                SaveFormat.Gen5BlackWhite or SaveFormat.Gen5Black2White2 => ReadGen5(core, sram, saved),
                _ => null,
            };
            return live is not null && Plausible(live, saved) ? live : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[DexHub] Live-Pokédex: " + ex.Message);
            return null;
        }
    }

    private bool Plausible(DexProgress live, DexProgress? saved)
    {
        if (live.Caught.Count > MaxSpecies || live.Seen.Count > MaxSpecies) return false;
        if (!live.Caught.IsSubsetOf(live.Seen)) return false;
        if (saved is not null && !saved.Caught.IsSubsetOf(live.Caught)) return false;
        return true;
    }

    // ---------- Speicher holen ----------

    /// <summary>Speicherbereich ab einer Systemadresse; zuerst über die Speicherkarte des Cores, sonst über "System-RAM".</summary>
    private static byte[]? Region(LibretroCore core, ulong address, int length, ulong systemRamBase)
    {
        var buffer = new byte[length];
        if (core.ReadAddress(address, buffer)) return buffer;
        var ram = core.SystemRam;
        long start = (long)(address - systemRamBase);
        if (start < 0 || start + length > ram.Length) return null;
        ram.Slice((int)start, length).CopyTo(buffer);
        return buffer;
    }

    private static byte[]? WholeRam(LibretroCore core, ulong address, int length)
    {
        var ram = core.SystemRam;
        if (ram.Length >= length / 2) return ram.ToArray();
        var buffer = new byte[length];
        return core.ReadAddress(address, buffer) ? buffer : null;
    }

    private static HashSet<int> Flags(ReadOnlySpan<byte> data, int count)
    {
        var set = new HashSet<int>();
        for (int n = 1; n <= count && (n - 1) / 8 < data.Length; n++)
            if ((data[(n - 1) / 8] & (1 << ((n - 1) % 8))) != 0) set.Add(n);
        return set;
    }

    // ---------- Gen 1 / 2 ----------

    private DexProgress? ReadGb(LibretroCore core, (int Caught, int Seen) addresses)
    {
        int bytes = (MaxSpecies + 7) / 8;
        var caught = Region(core, (ulong)addresses.Caught, bytes, 0xC000);
        var seen = Region(core, (ulong)addresses.Seen, bytes, 0xC000);
        if (caught is null || seen is null) return null;
        return new DexProgress(Flags(caught, MaxSpecies), Flags(seen, MaxSpecies));
    }

    // ---------- Gen 3 ----------

    private DexProgress? ReadGen3(LibretroCore core, byte[] sram)
    {
        // Ohne Spielstand kennen wir Name und ID nicht – dann erst nach dem ersten Speichern
        if (Gen3Save.Load(sram) is not { } save) return null;
        var (name, trainerId) = save.TrainerAnchor();
        var ewram = WholeRam(core, 0x02000000, 0x40000);
        if (ewram is null) return null;

        bool IsBlock(int o) => o >= 0 && o + 0x90 <= ewram.Length
                               && ewram.AsSpan(o, 8).SequenceEqual(name) && ewram.AsSpan(o + 0x0A, 4).SequenceEqual(trainerId);

        if (!IsBlock(_cachedOffset))
        {
            _cachedOffset = -1;
            for (int o = 0; o + 0x90 <= ewram.Length; o += 4)
                if (IsBlock(o)) { _cachedOffset = o; break; }
        }
        if (_cachedOffset < 0) return null;
        return new DexProgress(Flags(ewram.AsSpan(_cachedOffset + 0x28, 49), 386), Flags(ewram.AsSpan(_cachedOffset + 0x5C, 49), 386));
    }

    // ---------- Gen 4 ----------

    private DexProgress? ReadGen4(LibretroCore core, DexProgress? saved)
    {
        var ram = WholeRam(core, 0x02000000, 0x400000);
        if (ram is null) return null;

        DexProgress At(int o) => new(Flags(ram.AsSpan(o + 0x04, 64), 493), Flags(ram.AsSpan(o + 0x44, 64), 493));
        bool IsDex(int o) => o >= 0 && o + 0x84 <= ram.Length && BinaryPrimitives.ReadUInt32LittleEndian(ram.AsSpan(o)) == 0xBEEFCAFE;

        if (IsDex(_cachedOffset) && Plausible(At(_cachedOffset), saved)) return At(_cachedOffset);

        // Neu suchen; bei mehreren Treffern den mit den meisten Einträgen nehmen (der aktuelle Stand)
        DexProgress? best = null;
        for (int o = 0; o + 0x84 <= ram.Length; o += 4)
        {
            if (!IsDex(o)) continue;
            var candidate = At(o);
            if (!Plausible(candidate, saved)) continue;
            if (best is null || candidate.Caught.Count > best.Caught.Count)
            {
                best = candidate;
                _cachedOffset = o;
            }
        }
        return best;
    }

    // ---------- Gen 5 ----------

    private DexProgress? ReadGen5(LibretroCore core, byte[] sram, DexProgress? saved)
    {
        int dexOffset = _format == SaveFormat.Gen5BlackWhite ? 0x21600 : 0x21400;
        if (saved is null || sram.Length < dexOffset + 0x200) return null;
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(sram.AsSpan(dexOffset));
        if (magic == 0) return null;

        var ram = WholeRam(core, 0x02000000, 0x400000);
        if (ram is null) return null;

        DexProgress At(int o)
        {
            var caught = Flags(ram.AsSpan(o + 0x08, 0x54), 649);
            var seen = new HashSet<int>(caught);
            for (int r = 0; r < 4; r++) seen.UnionWith(Flags(ram.AsSpan(o + 0x5C + r * 0x54, 0x54), 649));
            return new DexProgress(caught, seen);
        }
        bool IsDex(int o) => o >= 0 && o + 0x1AC <= ram.Length && BinaryPrimitives.ReadUInt32LittleEndian(ram.AsSpan(o)) == magic;

        if (IsDex(_cachedOffset) && Plausible(At(_cachedOffset), saved)) return At(_cachedOffset);

        DexProgress? best = null;
        for (int o = 0; o + 0x1AC <= ram.Length; o += 4)
        {
            if (!IsDex(o)) continue;
            var candidate = At(o);
            if (!Plausible(candidate, saved)) continue;
            if (best is null || candidate.Caught.Count > best.Caught.Count)
            {
                best = candidate;
                _cachedOffset = o;
            }
        }
        return best;
    }
}
