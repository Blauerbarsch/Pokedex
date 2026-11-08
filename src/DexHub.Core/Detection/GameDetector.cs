using System.Text;
using DexHub.Core.Models;

namespace DexHub.Core.Detection;

/// <summary>Erkennt anhand von Dateiendung und ROM-Header, um welches Spiel es sich handelt.</summary>
public static class GameDetector
{
    private static readonly Dictionary<string, Platform> ExtensionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".min"] = Platform.PokemonMini,
        [".gb"]  = Platform.GameBoy,
        [".gbc"] = Platform.GameBoyColor,
        [".gba"] = Platform.GameBoyAdvance,
        [".z64"] = Platform.Nintendo64,
        [".n64"] = Platform.Nintendo64,
        [".v64"] = Platform.Nintendo64,
        [".nds"] = Platform.NintendoDS,
        [".iso"] = Platform.GameCube, // GameCube oder Wii, wird per Header unterschieden
        [".gcm"] = Platform.GameCube,
        [".rvz"] = Platform.GameCube,
        [".ciso"] = Platform.GameCube,
        [".wbfs"] = Platform.Wii,
        [".3ds"] = Platform.Nintendo3DS,
        [".cci"] = Platform.Nintendo3DS,
        [".cxi"] = Platform.Nintendo3DS,
        [".cia"] = Platform.Nintendo3DS,
    };

    public static IReadOnlyCollection<string> SupportedExtensions => ExtensionMap.Keys;

    /// <summary>
    /// Ordner ohne Zugriffsrechte und versteckte Ordner (z. B. ".ipfs", ".Trash") werden übersprungen,
    /// statt das Programm abstürzen zu lassen – wichtig bei Netzlaufwerken.
    /// </summary>
    private static readonly EnumerationOptions ScanOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    /// <summary>Durchsucht einen Ordner (inkl. Unterordner) nach Spielen.</summary>
    public static List<GameEntry> ScanFolder(string folder)
    {
        var result = new List<GameEntry>();
        if (!Directory.Exists(folder)) return result;

        foreach (var file in Directory.EnumerateFiles(folder, "*", ScanOptions))
        {
            var entry = Detect(file);
            if (entry is not null) result.Add(entry);
        }
        return result.OrderBy(e => e.Info.Platform).ThenBy(e => e.Title).ToList();
    }

    /// <summary>Gibt null zurück, wenn die Datei keine unterstützte ROM ist.</summary>
    public static GameEntry? Detect(string path)
    {
        if (!ExtensionMap.TryGetValue(Path.GetExtension(path), out var platform))
            return null;

        GameInfo? info = null;
        try
        {
            info = platform switch
            {
                Platform.GameBoy or Platform.GameBoyColor => DetectGameBoy(path),
                Platform.GameBoyAdvance => DetectByCode(path, 0xAC, GameDatabase.GameBoyAdvance),
                Platform.NintendoDS => DetectByCode(path, 0x0C, GameDatabase.NintendoDS),
                Platform.Nintendo64 => DetectN64(path),
                Platform.GameCube or Platform.Wii => DetectByCode(path, 0x00, GameDatabase.Disc),
                _ => null, // Pokémon Mini und 3DS: nur über Dateinamen
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Datei nicht lesbar -> unten Fallback über den Dateinamen
        }

        if (info is not null)
            return new GameEntry { RomPath = path, Info = info, IsRecognized = true };

        // Fallback: Titel aus dem Dateinamen
        var fallback = new GameInfo(Path.GetFileNameWithoutExtension(path), platform, null);
        return new GameEntry { RomPath = path, Info = fallback, IsRecognized = false };
    }

    private static GameInfo? DetectGameBoy(string path)
    {
        var title = ReadAscii(path, 0x134, 16);
        foreach (var (prefix, info) in GameDatabase.GameBoy)
            if (title.StartsWith(prefix, StringComparison.Ordinal))
                return info;
        return null;
    }

    private static GameInfo? DetectByCode(string path, long offset, Dictionary<string, GameInfo> table)
    {
        var code = ReadAscii(path, offset, 4);
        return code.Length >= 3 && table.TryGetValue(code[..3], out var info) ? info : null;
    }

    private static GameInfo? DetectN64(string path)
    {
        var header = ReadBytes(path, 0, 0x40);
        if (header.Length < 0x40) return null;

        // N64-ROMs gibt es in drei Byte-Reihenfolgen. Wir wandeln alles in .z64 (Big-Endian) um.
        if (header[0] == 0x37 && header[1] == 0x80)          // .v64: 16-Bit vertauscht
            for (int i = 0; i < header.Length; i += 2)
                (header[i], header[i + 1]) = (header[i + 1], header[i]);
        else if (header[0] == 0x40 && header[1] == 0x12)     // .n64: 32-Bit Little-Endian
            for (int i = 0; i < header.Length; i += 4)
                Array.Reverse(header, i, 4);

        var title = Encoding.ASCII.GetString(header, 0x20, 20).TrimEnd('\0', ' ');
        foreach (var (prefix, info) in GameDatabase.Nintendo64)
            if (title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return info;
        return null;
    }

    private static byte[] ReadBytes(string path, long offset, int count)
    {
        using var fs = File.OpenRead(path);
        if (fs.Length < offset + count) return [];
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[count];
        fs.ReadExactly(buffer);
        return buffer;
    }

    private static string ReadAscii(string path, long offset, int count)
    {
        var bytes = ReadBytes(path, offset, count);
        return Encoding.ASCII.GetString(bytes).TrimEnd('\0', ' ');
    }
}
