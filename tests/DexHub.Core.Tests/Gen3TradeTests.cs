using System.Buffers.Binary;
using DexHub.Core.Pokedex;
using DexHub.Core.Pokemon;
using DexHub.Core.Trading;

namespace DexHub.Core.Tests;

public class Gen3TradeTests
{
    private static readonly int[] Sizes =
        [0xF2C, 0xF80, 0xF80, 0xF80, 0xF08, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0x7D0];

    /// <summary>Leerer, gültiger 128-KB-Spielstand; Abschnitte rotiert wie im echten Spiel.</summary>
    private static byte[] BlankSave(uint gameCode = 1)
    {
        var data = new byte[0x20000];
        for (int s = 0; s < 14; s++)
        {
            int id = (s + 3) % 14;
            int sector = s * 0x1000;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(sector + 0xFF4), (ushort)id);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(sector + 0xFF8), 0x08012025);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(sector + 0xFFC), 7);
            if (id == 0) BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(sector + 0xAC), gameCode);
        }
        FixChecksums(data);
        return data;
    }

    private static void FixChecksums(byte[] data)
    {
        for (int s = 0; s < 14; s++)
        {
            int sector = s * 0x1000;
            int id = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(sector + 0xFF4));
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(sector + 0xFF6),
                Gen3TradeSave.Checksum(data.AsSpan(sector, Sizes[id])));
        }
    }

    private static byte[] Pk3(uint pid, int speciesIndex, string nickname, byte language = 5)
    {
        var raw = new byte[80];
        BinaryPrimitives.WriteUInt32LittleEndian(raw, pid);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), 0x1234_5678);
        Gen3Save.EncodeString(nickname, 10)!.CopyTo(raw, 0x08);
        raw[0x12] = language;
        var data = new byte[48];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(Gen3Save.BlockOffset(pid, 'G')), (ushort)speciesIndex);
        return Gen3Save.Encrypt(raw, data);
    }

    [Fact]
    public void Writes_pokemon_across_sector_border_and_evolves_on_trade()
    {
        var gameData = new GameData();
        var save = Gen3TradeSave.Load(BlankSave(), "Feuerrot", gameData, out var error);
        Assert.NotNull(save);
        Assert.Equal("", error);

        // Box 2, Platz 20 liegt genau über der Grenze zwischen Abschnitt 5 und 6
        save.SetRaw(1, 19, Pk3(0xABCDEF01, 64, "KADABRA"));
        var names = new SpeciesNames(id => id == 64 ? "Kadabra" : "Simsala", id => id == 64 ? "Kadabra" : "Alakazam");
        var notes = new List<string>();
        var (raw, species) = save.Evolve(save.GetRaw(1, 19), true, names, notes);
        Assert.Equal(65, species);
        save.SetRaw(1, 19, raw);
        save.RegisterCaught(65);

        var path = Path.GetTempFileName();
        save.Save(path);
        var reloaded = Gen3TradeSave.Load(File.ReadAllBytes(path), "Feuerrot", gameData, out error);
        Assert.NotNull(reloaded);

        var (box, slot, p) = Assert.Single(reloaded.BoxPokemon());
        Assert.Equal((1, 19), (box, slot));
        Assert.Equal(65, p.Species);
        Assert.Equal("SIMSALA", p.Nickname);
        Assert.Equal(0xABCDEF01u, reloaded.PidOf(reloaded.GetRaw(1, 19)));

        // Pokédex über den Gen-3-Leser prüfen
        var dex = Gen3Save.Load(File.ReadAllBytes(path))!.ReadDex();
        Assert.Contains(65, dex.Caught);
        File.Delete(path);
    }

    [Fact]
    public void Refuses_save_with_wrong_checksum()
    {
        var data = BlankSave();
        data[0x10] ^= 0xFF; // Sektor 0 verändern, ohne Prüfsumme anzupassen
        data[0x0E010] ^= 0xFF; // und in der zweiten Kopie keine gültigen Sektoren
        Assert.Null(Gen3TradeSave.Load(data, "Rubin", new GameData(), out var error));
        Assert.NotEqual("", error);
    }
}
