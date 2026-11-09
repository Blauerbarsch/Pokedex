using DexHub.Core.Models;

namespace DexHub.Core.Saves;

/// <summary>
/// Prüft, ob ein Gen-1/2-Spielstand echt ist (Prüfsumme des Spiels stimmt).
/// Ein frischer, noch nie gespeicherter Speicher ist mit Füllwerten belegt – den darf man nicht
/// als Pokédex lesen, sonst sieht es aus, als wären alle Pokémon gefangen.
/// </summary>
public static class SaveValidator
{
    public static bool IsValidGen12(byte[] data, SaveFormat format)
    {
        if (data.Length < 0x8000) return false;
        return format switch
        {
            // Gen 1: ein Byte bei 0x3523 = invertierte Summe aller Bytes von 0x2598 bis 0x3522
            SaveFormat.Gen1 => (byte)~Sum(data, 0x2598, 0x3522) == data[0x3523],
            // Gen 2: 16-Bit-Summe als Little-Endian
            SaveFormat.Gen2GoldSilver => (ushort)Sum(data, 0x2009, 0x2D68) == (data[0x2D69] | data[0x2D6A] << 8),
            SaveFormat.Gen2Crystal => (ushort)Sum(data, 0x2009, 0x2B82) == (data[0x2D0D] | data[0x2D0E] << 8),
            _ => true,
        };
    }

    private static int Sum(byte[] data, int from, int toInclusive)
    {
        int sum = 0;
        for (int i = from; i <= toInclusive; i++) sum += data[i];
        return sum;
    }
}
