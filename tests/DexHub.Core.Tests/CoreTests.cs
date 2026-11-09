using System.Text;
using DexHub.Core.Detection;
using DexHub.Core.Models;
using DexHub.Core.Pokedex;
using DexHub.Core.Pokemon;
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
    public void Gen4_uses_the_newer_partition()
    {
        var save = new byte[0x80000];
        const int dex = 0x12B8; // HeartGold/SoulSilver

        // Alte Kopie: nur Endivie (#152)
        BitConverter.GetBytes(0xBEEFCAFE).CopyTo(save, dex);
        SetFlag(save, dex + 0x04, 152);
        // Neue Kopie: Endivie und Lugia (#249)
        BitConverter.GetBytes(0xBEEFCAFE).CopyTo(save, 0x40000 + dex);
        SetFlag(save, 0x40000 + dex + 0x04, 152);
        SetFlag(save, 0x40000 + dex + 0x04, 249);

        var result = SaveReader.Parse(save, SaveFormat.Gen4HeartGoldSoulSilver)!;
        Assert.Equal(new[] { 152, 249 }, result.Caught.Order().ToArray());
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
    public void Only_pokemon_games_pass_the_filter()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "Pokémon Platin (Germany).nds"), new byte[0x200]);
        File.WriteAllBytes(Path.Combine(dir, "Mario Kart DS.nds"), new byte[0x200]);
        var gba = new byte[0x200];
        Encoding.ASCII.GetBytes("BPRD").CopyTo(gba, 0xAC); // Feuerrot, Dateiname egal
        File.WriteAllBytes(Path.Combine(dir, "spiel123.gba"), gba);

        var titles = GameDetector.ScanFolder(dir).Select(g => g.Title).ToList();

        Assert.Equal(2, titles.Count);
        Assert.Contains("Pokémon Feuerrote Edition", titles);
        Assert.Contains("Pokémon Platin (Germany)", titles);
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

public class Gen4PokemonTests
{
    /// <summary>Baut ein verschlüsseltes PK4 genau so, wie das Spiel es speichert.</summary>
    private static byte[] EncryptPk4(uint pid, Action<byte[]> fill)
    {
        var d = new byte[136]; // Blöcke A–D in Reihenfolge ab 0x08
        fill(d);
        BitConverter.GetBytes(pid).CopyTo(d, 0);

        ushort sum = 0;
        for (int i = 8; i < 136; i += 2) sum = unchecked((ushort)(sum + BitConverter.ToUInt16(d, i)));
        BitConverter.GetBytes(sum).CopyTo(d, 6);

        // PID 0 -> Reihenfolge ABCD, also nur verschlüsseln
        uint seed = sum;
        for (int i = 8; i < 136; i += 2)
        {
            seed = unchecked(seed * 0x41C64E6D + 0x6073);
            ushort word = (ushort)(BitConverter.ToUInt16(d, i) ^ (seed >> 16));
            BitConverter.GetBytes(word).CopyTo(d, i);
        }
        return d;
    }

    [Fact]
    public void Reads_party_pokemon_from_platinum_save()
    {
        var pk4 = EncryptPk4(0, d =>
        {
            BitConverter.GetBytes((ushort)390).CopyTo(d, 0x08); // Panflam
            BitConverter.GetBytes(135u).CopyTo(d, 0x10);         // EP
            BitConverter.GetBytes((ushort)10).CopyTo(d, 0x28);  // Kratzer
            d[0x7B] = 26; d[0x7C] = 11; d[0x7D] = 8;             // gefangen am 08.11.2026
            d[0x83] = 4;                                          // Pokéball
        });

        var save = new byte[0x80000];
        save[0x9C] = 1;                 // ein Pokémon im Team
        pk4.CopyTo(save, 0xA0);

        var list = Gen4PokemonReader.Read(save, SaveFormat.Gen4Platinum, 0, "Platin");

        var p = Assert.Single(list);
        Assert.Equal(390, p.Species);
        Assert.Equal(135u, p.Exp);
        Assert.Equal(10, p.Moves[0]);
        Assert.Equal(new DateOnly(2026, 11, 8), p.MetDate);
        Assert.Equal(4, p.Ball);
        Assert.Equal("Team", p.Location);
    }

    [Fact]
    public void Decodes_gen4_text()
    {
        // "Flammi" in der Gen-4-Zeichentabelle, dann Ende (0xFFFF)
        ushort[] chars = [0x0130, 0x0150, 0x0145, 0x0151, 0x0151, 0x014D, 0xFFFF];
        var bytes = chars.SelectMany(c => BitConverter.GetBytes(c)).ToArray();
        Assert.Equal("Flammi", Gen4PokemonReader.DecodeString(bytes));
    }
}

public class Gen4TradeTests
{
    [Fact]
    public void Crc16_matches_known_value()
    {
        // CRC-16/CCITT-FALSE von "123456789" ist 0x29B1
        Assert.Equal(0x29B1, DexHub.Core.Trading.Gen4Save.Crc16("123456789"u8));
    }

    [Fact]
    public void Encrypt_then_decrypt_roundtrips_for_all_block_orders()
    {
        for (uint pid = 0; pid < 24u << 13; pid += 1u << 13)
        {
            var d = new byte[136];
            BitConverter.GetBytes(pid).CopyTo(d, 0);
            for (int i = 8; i < 136; i++) d[i] = (byte)(i * 7);
            var encrypted = Gen4PokemonReader.Encrypt(d);
            var back = Gen4PokemonReader.Decrypt(encrypted)!;
            Assert.Equal(d[8..], back[8..]);
        }
    }
}

public class Gen123Tests
{
    [Fact]
    public void Decodes_gen1_text()
    {
        // "PIKA" + Ende
        byte[] data = [0x8F, 0x88, 0x8A, 0x80, 0x50];
        Assert.Equal("PIKA", Gen12PokemonReader.DecodeString(data));
    }

    [Fact]
    public void Decodes_gen3_text()
    {
        // "Flo" + Ende
        byte[] data = [0xC0, 0xE0, 0xE3, 0xFF];
        Assert.Equal("Flo", Gen3Save.DecodeString(data));
    }

    [Fact]
    public void Reads_gen2_party_pokemon()
    {
        var save = new byte[0x8000];
        int party = 0x288A;                 // Gold/Silber
        save[party] = 1;                    // ein Pokémon
        save[party + 1] = 155;              // Artenliste: Feurigel
        save[party + 2] = 0xFF;
        int mon = party + 8;
        save[mon] = 155;                    // Art
        save[mon + 0x02] = 33;              // Tackle
        save[mon + 0x1F] = 5;               // Level 5
        save[mon + 0x15] = 0xAA; save[mon + 0x16] = 0xAA; // DVs 10/10/10/10 -> schillernd
        save[0x2724] = 0;                   // aktuelle Box 1 (leer)

        var list = Gen12PokemonReader.Read(save, SaveFormat.Gen2GoldSilver, "Gold", i => i);

        var p = Assert.Single(list);
        Assert.Equal(155, p.Species);
        Assert.Equal(5, p.Level);
        Assert.True(p.IsShiny);
        Assert.Equal("Team", p.Location);
    }
}
