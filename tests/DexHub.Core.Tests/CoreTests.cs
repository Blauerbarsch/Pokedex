using System.Text;
using DexHub.Core.Detection;
using DexHub.Core.Models;
using DexHub.Core.Pokedex;
using DexHub.Core.Saves;

namespace DexHub.Core.Tests;

public class SaveReaderTests
{
    [Fact]
    public void Gen1_reads_caught_and_seen_flags()
    {
        var save = new byte[0x8000];
        // Bisasam (#1) und Mew (#151) gefangen, Pikachu (#25) nur gesehen
        SetFlag(save, 0x25A3, 1);
        SetFlag(save, 0x25A3, 151);
        SetFlag(save, 0x25B6, 1);
        SetFlag(save, 0x25B6, 25);
        SetFlag(save, 0x25B6, 151);

        var dex = SaveReader.Parse(save, SaveFormat.Gen1)!;

        Assert.Equal(new[] { 1, 151 }, dex.Caught.Order().ToArray());
        Assert.Equal(new[] { 1, 25, 151 }, dex.Seen.Order().ToArray());
    }

    [Fact]
    public void Too_small_file_returns_null()
    {
        Assert.Null(SaveReader.Parse(new byte[100], SaveFormat.Gen1));
    }

    private static void SetFlag(byte[] data, int offset, int dexNumber)
    {
        int i = dexNumber - 1;
        data[offset + i / 8] |= (byte)(1 << (i % 8));
    }
}

public class GameDetectorTests
{
    [Fact]
    public void Detects_gameboy_title()
    {
        var rom = new byte[0x8000];
        Encoding.ASCII.GetBytes("POKEMON YELLOW").CopyTo(rom, 0x134);
        var path = WriteTemp(rom, ".gbc");

        var game = GameDetector.Detect(path)!;

        Assert.True(game.IsRecognized);
        Assert.Equal(SaveFormat.Gen1, game.Info.SaveFormat);
    }

    [Fact]
    public void Detects_gba_game_code()
    {
        var rom = new byte[0x200];
        Encoding.ASCII.GetBytes("BPED").CopyTo(rom, 0xAC); // Smaragd, deutsch
        var game = GameDetector.Detect(WriteTemp(rom, ".gba"))!;

        Assert.Equal("Pokémon Smaragd-Edition", game.Title);
    }

    [Fact]
    public void Unknown_rom_falls_back_to_filename()
    {
        var game = GameDetector.Detect(WriteTemp(new byte[0x200], ".nds"))!;
        Assert.False(game.IsRecognized);
        Assert.Equal(Platform.NintendoDS, game.Info.Platform);
    }

    [Fact]
    public void Ignores_unsupported_files()
    {
        Assert.Null(GameDetector.Detect(WriteTemp([1, 2, 3], ".txt")));
    }

    private static string WriteTemp(byte[] data, string ext)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ext);
        File.WriteAllBytes(path, data);
        return path;
    }
}

public class CsvTests
{
    [Fact]
    public void Parses_quoted_fields()
    {
        var rows = Csv.Parse("id,name\n1,\"Mr. Mime, der Clown\"\n2,Pikachu\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal("Mr. Mime, der Clown", rows[0]["name"]);
        Assert.Equal("Pikachu", rows[1]["name"]);
    }
}
