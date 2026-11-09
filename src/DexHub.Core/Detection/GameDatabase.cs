using DexHub.Core.Models;

namespace DexHub.Core.Detection;

/// <summary>
/// Bekannte Pokémon-Spiele, erkannt über Kennungen im ROM-Header.
/// Neue Spiele einfach unten in die Listen eintragen.
/// </summary>
public static class GameDatabase
{
    // Game Boy / Color: Titel im Header ab 0x134. Reihenfolge wichtig: längere Präfixe zuerst.
    public static readonly (string TitlePrefix, GameInfo Info)[] GameBoy =
    [
        ("POKEMON RED",    new("Pokémon Rote Edition",   Platform.GameBoy, 1, SaveFormat.Gen1, Version: 1)),
        ("POKEMON BLUE",   new("Pokémon Blaue Edition",  Platform.GameBoy, 1, SaveFormat.Gen1, Version: 2)),
        ("POKEMON YELLOW", new("Pokémon Gelbe Edition",  Platform.GameBoy, 1, SaveFormat.Gen1, Version: 3)),
        ("POKEMON GREEN",  new("Pokémon Grüne Edition (JP)", Platform.GameBoy, 1)), // JP-Spielstände haben anderes Layout
        ("POKEMON_GLD",    new("Pokémon Goldene Edition",  Platform.GameBoyColor, 2, SaveFormat.Gen2GoldSilver, Version: 4)),
        ("POKEMON_SLV",    new("Pokémon Silberne Edition", Platform.GameBoyColor, 2, SaveFormat.Gen2GoldSilver, Version: 5)),
        ("PM_CRYSTAL",     new("Pokémon Kristall-Edition", Platform.GameBoyColor, 2, SaveFormat.Gen2Crystal, Version: 6)),
        ("POKEMON PINBALL",new("Pokémon Pinball",          Platform.GameBoyColor, null)),
    ];

    // GBA: 4-stelliger Spielcode ab 0xAC. Wir vergleichen die ersten 3 Zeichen (4. = Region).
    public static readonly Dictionary<string, GameInfo> GameBoyAdvance = new()
    {
        ["AXV"] = new("Pokémon Rubin-Edition",     Platform.GameBoyAdvance, 3, SaveFormat.Gen3, Version: 7),
        ["AXP"] = new("Pokémon Saphir-Edition",    Platform.GameBoyAdvance, 3, SaveFormat.Gen3, Version: 8),
        ["BPR"] = new("Pokémon Feuerrote Edition", Platform.GameBoyAdvance, 3, SaveFormat.Gen3, Version: 10),
        ["BPG"] = new("Pokémon Blattgrüne Edition",Platform.GameBoyAdvance, 3, SaveFormat.Gen3, Version: 11),
        ["BPE"] = new("Pokémon Smaragd-Edition",   Platform.GameBoyAdvance, 3, SaveFormat.Gen3, Version: 9),
        ["BPP"] = new("Pokémon Pinball: Rubin & Saphir", Platform.GameBoyAdvance, null),
        ["B24"] = new("Pokémon Mystery Dungeon: Team Rot", Platform.GameBoyAdvance, null),
    };

    // NDS: 4-stelliger Spielcode ab 0x0C.
    public static readonly Dictionary<string, GameInfo> NintendoDS = new()
    {
        ["ADA"] = new("Pokémon Diamant-Edition",   Platform.NintendoDS, 4, SaveFormat.Gen4DiamondPearl, Version: 12),
        ["APA"] = new("Pokémon Perl-Edition",      Platform.NintendoDS, 4, SaveFormat.Gen4DiamondPearl, Version: 13),
        ["CPU"] = new("Pokémon Platin-Edition",    Platform.NintendoDS, 4, SaveFormat.Gen4Platinum, Version: 14),
        ["IPK"] = new("Pokémon HeartGold",         Platform.NintendoDS, 4, SaveFormat.Gen4HeartGoldSoulSilver, Version: 15),
        ["IPG"] = new("Pokémon SoulSilver",        Platform.NintendoDS, 4, SaveFormat.Gen4HeartGoldSoulSilver, Version: 16),
        ["IRB"] = new("Pokémon Schwarze Edition",  Platform.NintendoDS, 5, SaveFormat.None, Version: 17),
        ["IRA"] = new("Pokémon Weiße Edition",     Platform.NintendoDS, 5, SaveFormat.None, Version: 18),
        ["IRE"] = new("Pokémon Schwarze Edition 2",Platform.NintendoDS, 5, SaveFormat.None, Version: 21),
        ["IRD"] = new("Pokémon Weiße Edition 2",   Platform.NintendoDS, 5, SaveFormat.None, Version: 22),
    };

    // GameCube/Wii: 6-stellige Disc-ID ab 0x00, wir vergleichen die ersten 3 Zeichen.
    public static readonly Dictionary<string, GameInfo> Disc = new()
    {
        ["GC6"] = new("Pokémon Colosseum",               Platform.GameCube, 3, SaveFormat.None, Version: 19),
        ["GXX"] = new("Pokémon XD: Der dunkle Sturm",    Platform.GameCube, 3, SaveFormat.None, Version: 20),
        ["RPB"] = new("Pokémon Battle Revolution",       Platform.Wii, 4),
    };

    // N64: Name im Header ab 0x20 (nach Umwandlung in Big-Endian).
    public static readonly (string TitlePrefix, GameInfo Info)[] Nintendo64 =
    [
        ("POKEMON STADIUM 2", new("Pokémon Stadium 2", Platform.Nintendo64, 2)),
        ("POKEMON STADIUM",   new("Pokémon Stadium",   Platform.Nintendo64, 1)),
        ("POKEMON SNAP",      new("Pokémon Snap",      Platform.Nintendo64, null)),
        ("POKEMON PUZZLE",    new("Pokémon Puzzle League", Platform.Nintendo64, null)),
    ];
}
