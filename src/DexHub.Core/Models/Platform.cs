namespace DexHub.Core.Models;

/// <summary>Alle Konsolen, auf denen es Pokémon-Spiele gibt (ohne Switch).</summary>
public enum Platform
{
    Unknown,
    PokemonMini,
    GameBoy,
    GameBoyColor,
    GameBoyAdvance,
    Nintendo64,
    NintendoDS,
    GameCube,
    Wii,
    Nintendo3DS,
}

public static class PlatformExtensions
{
    public static string DisplayName(this Platform p) => p switch
    {
        Platform.PokemonMini => "Pokémon Mini",
        Platform.GameBoy => "Game Boy",
        Platform.GameBoyColor => "Game Boy Color",
        Platform.GameBoyAdvance => "Game Boy Advance",
        Platform.Nintendo64 => "Nintendo 64",
        Platform.NintendoDS => "Nintendo DS",
        Platform.GameCube => "GameCube",
        Platform.Wii => "Wii",
        Platform.Nintendo3DS => "Nintendo 3DS",
        _ => "Unbekannt",
    };
}
