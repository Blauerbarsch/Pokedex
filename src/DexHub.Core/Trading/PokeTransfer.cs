using System.Buffers.Binary;
using DexHub.Core.Pokemon;

namespace DexHub.Core.Trading;

/// <summary>
/// Wandelt ein Gen-4-Pokémon in ein Gen-5-Pokémon um – wie der PokéTransfer in Schwarz/Weiß.
/// Nach PKHeX (PK4.ConvertToPK5): gleiche Grundstruktur; Texte werden UTF-16, das Wesen wird extra gespeichert,
/// Fundort "Poké-Transfer", Fundlevel = aktuelles Level, Freundschaft 70, VM-Attacken werden entfernt.
/// </summary>
public static class PokeTransfer
{
    private const ushort TransferLocation = 30001;
    private const int Arceus = 493, Shedinja = 292;
    private const int Cut = 15, Fly = 19, Surf = 57, Strength = 70, Waterfall = 127, RockSmash = 249,
        Whirlpool = 250, RockClimb = 431, Defog = 432;

    public static byte[] Convert(byte[] pk4, Func<int, uint, int> levelFromExp, List<string> notes)
    {
        var d = Gen4PokemonReader.Decrypt(pk4) ?? throw new InvalidOperationException("Die Pokémon-Daten sind beschädigt.");
        uint pid = BinaryPrimitives.ReadUInt32LittleEndian(d);
        int species = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(0x08));
        if ((BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(0x38)) >> 30 & 1) == 1)
            throw new InvalidOperationException("Eier können nicht übertragen werden – erst ausbrüten lassen.");

        // Texte vorher lesen (Gen-4-Zeichensatz)
        var nickname = Gen4PokemonReader.DecodeString(d.AsSpan(0x48, 22));
        var trainer = Gen4PokemonReader.DecodeString(d.AsSpan(0x68, 16));
        int ball = d[0x86] != 0 ? d[0x86] : d[0x83]; // HG/SS speichern neue Bälle extra

        d[0x14] = 70; // Freundschaft

        int item = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(0x0A));
        bool mail = item is >= 137 and <= 148;
        if (species == Arceus)
        {
            d[0x40] &= 0x07; // Form zurücksetzen, Tafel wird entfernt
            item = 0;
        }
        if (mail || item > 536)
        {
            notes.Add("Der getragene Gegenstand bleibt zurück.");
            item = 0;
        }
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(0x0A), (ushort)item);

        d[0x41] = (byte)(pid % 25);            // Wesen jetzt extra gespeichert
        d[0x42] = 0;                           // keine versteckte Fähigkeit
        d.AsSpan(0x44, 4).Clear();             // Platin/HGSS-Fundorte entfernen
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(0x80), TransferLocation);
        d[0x86] = 0;
        d[0x87] = 0;
        d[0x83] = (byte)ball;

        // Texte in UTF-16
        Gen5PokemonReader.EncodeString(nickname, 22).CopyTo(d.AsSpan(0x48));
        Gen5PokemonReader.EncodeString(trainer, 16).CopyTo(d.AsSpan(0x68));

        // Fundlevel = aktuelles Level, Datum heute
        uint exp = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(0x10));
        int level = Math.Clamp(levelFromExp(species, exp), 1, 100);
        d[0x84] = (byte)((d[0x84] & 0x80) | level);
        var today = DateTime.Today;
        d[0x7B] = (byte)(today.Year - 2000);
        d[0x7C] = (byte)today.Month;
        d[0x7D] = (byte)today.Day;

        // VM-Attacken entfernen (Auflockern bleibt, wenn vorhanden – dann fällt Whirlpool weg)
        var moves = Enumerable.Range(0, 4).Select(i => (int)BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(0x28 + i * 2))).ToArray();
        bool hasDefog = moves.Contains(Defog);
        int[] banned = hasDefog
            ? [Cut, Fly, Surf, Strength, Whirlpool, RockSmash, Waterfall, RockClimb]
            : [Cut, Fly, Surf, Strength, Defog, RockSmash, Waterfall, RockClimb];
        var keptMoves = new List<(int Move, byte Pp, byte PpUps)>();
        for (int i = 0; i < 4; i++)
            if (moves[i] != 0 && !banned.Contains(moves[i])) keptMoves.Add((moves[i], d[0x30 + i], d[0x34 + i]));
        if (keptMoves.Count < moves.Count(m => m != 0)) notes.Add("VM-Attacken wurden vergessen (wie beim echten PokéTransfer).");
        if (keptMoves.Count == 0 && moves.Any(m => m != 0)) keptMoves.Add((33, 35, 0)); // Tackle als Notlösung
        for (int i = 0; i < 4; i++)
        {
            var (move, pp, ups) = i < keptMoves.Count ? keptMoves[i] : (0, (byte)0, (byte)0);
            BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(0x28 + i * 2), (ushort)move);
            d[0x30 + i] = pp;
            d[0x34 + i] = ups;
        }

        if (species == Shedinja) d[0x40] = (byte)((d[0x40] & ~0x06) | 0b100); // geschlechtslos

        return Gen4PokemonReader.Encrypt(d);
    }
}
