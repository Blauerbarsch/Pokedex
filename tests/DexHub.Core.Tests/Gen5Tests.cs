using System.Buffers.Binary;
using DexHub.Core.Models;
using DexHub.Core.Pokemon;
using DexHub.Core.Saves;
using DexHub.Core.Trading;

namespace DexHub.Core.Tests;

public class Gen5Tests
{
    private static void FixBlock(byte[] data, int offset, int length, int mirror)
    {
        var crc = Gen4Save.Crc16(data.AsSpan(offset, length));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset + length + 2), crc);
        if (mirror >= 0) BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(mirror), crc);
    }

    /// <summary>Leerer Schwarz/Weiß-Spielstand mit gültigen Prüfsummen für Pokédex und Tabelle.</summary>
    private static byte[] BlankBlackWhite()
    {
        var data = new byte[0x80000];
        FixBlock(data, 0x21600, 0x4D4, 0x23F00 + 55 * 2);
        var table = Gen4Save.Crc16(data.AsSpan(0x23F00, 0x8C));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x23F9A), table);
        return data;
    }

    private static byte[] Pk(int species, uint pid, string nickname, bool gen5)
    {
        var d = new byte[136];
        BinaryPrimitives.WriteUInt32LittleEndian(d, pid);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(0x08), (ushort)species);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(0x10), 1000);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(0x28), 57); // Surfer
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(0x2A), 85); // Donnerblitz
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(0x38), 31u | 1u << 31); // KP-IS 31, Spitzname
        d[0x17] = 5;
        (gen5 ? Gen5PokemonReader.EncodeString(nickname, 22) : Gen4PokemonReader.EncodeString(nickname, 22)).CopyTo(d, 0x48);
        (gen5 ? Gen5PokemonReader.EncodeString("Ash", 16) : Gen4PokemonReader.EncodeString("Ash", 16)).CopyTo(d, 0x68);
        d[0x83] = 4;
        if (gen5) d[0x41] = (byte)(pid % 25);
        return Gen4PokemonReader.Encrypt(d);
    }

    [Fact]
    public void Reads_and_writes_black_white_save()
    {
        var save = Gen5Save.Load(BlankBlackWhite(), SaveFormat.Gen5BlackWhite, "Schwarz", out var error);
        Assert.NotNull(save);
        Assert.Equal("", error);
        Assert.Empty(save.ReadDex().Caught);

        save.SetRaw(3, 7, Pk(495, 0xDEADBEEF, "Serpi", gen5: true)); // Serpifeu
        save.RegisterCaught(495);
        var path = Path.GetTempFileName();
        save.Save(path);

        var bytes = File.ReadAllBytes(path);
        var reloaded = Gen5Save.Load(bytes, SaveFormat.Gen5BlackWhite, "Schwarz", out error);
        Assert.NotNull(reloaded);
        Assert.Contains(495, reloaded.ReadDex().Caught);
        Assert.Contains(495, SaveReader.Parse(bytes, SaveFormat.Gen5BlackWhite)!.Seen);

        var (box, slot, p) = Assert.Single(reloaded.BoxPokemon());
        Assert.Equal((3, 7), (box, slot));
        Assert.Equal(495, p.Species);
        Assert.Equal("Serpi", p.Nickname);
        Assert.Equal(0xDEADBEEF % 25, (uint)p.Nature);

        // Box-Prüfsumme und Spiegel in der Tabelle stimmen
        var crc = Gen4Save.Crc16(bytes.AsSpan(0x400 + 3 * 0x1000, 0xFF0));
        Assert.Equal(crc, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0x400 + 3 * 0x1000 + 0xFF2)));
        Assert.Equal(crc, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0x23F00 + 4 * 2)));
        File.Delete(path);
    }

    [Fact]
    public void Refuses_unsaved_game()
    {
        var data = BlankBlackWhite();
        data[0x21610] = 1; // Pokédex geändert, Prüfsumme nicht
        Assert.Null(Gen5Save.Load(data, SaveFormat.Gen5BlackWhite, "Weiß", out _));
    }

    [Fact]
    public void Poke_transfer_converts_gen4_to_gen5()
    {
        var notes = new List<string>();
        var pk5 = PokeTransfer.Convert(Pk(25, 0x12345678, "Blitz", gen5: false), (_, _) => 17, notes);
        var p = Gen5PokemonReader.Parse(pk5, "Weiß", "Box 1");
        Assert.NotNull(p);
        Assert.Equal(25, p.Species);
        Assert.Equal("Blitz", p.Nickname);
        Assert.Equal("Ash", p.TrainerName);
        Assert.Equal(85, p.Moves[0]);       // Surfer (VM) wurde entfernt, Donnerblitz rückt nach
        Assert.Equal(0, p.Moves[1]);
        Assert.Equal(17, p.MetLevel);
        Assert.Equal((int)(0x12345678u % 25), p.Nature);
        Assert.Contains(notes, n => n.Contains("VM"));
    }
}
