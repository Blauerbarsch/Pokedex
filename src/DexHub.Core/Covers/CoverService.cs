using System.Text;
using System.Text.Json;
using DexHub.Core.Models;
using DexHub.Core.Settings;

namespace DexHub.Core.Covers;

/// <summary>
/// Lädt Cover-Bilder aus der freien Sammlung libretro-thumbnails (https://github.com/libretro-thumbnails).
/// Die Dateien sind nach den offiziellen "No-Intro"-Namen benannt, z. B.
/// "Pokemon - Platinum Version (USA).png". Deshalb kennt jedes Spiel seinen englischen Namen.
/// </summary>
public sealed class CoverService
{
    /// <summary>Deutscher Titel aus der GameDatabase -> Anfang des englischen No-Intro-Namens.</summary>
    private static readonly Dictionary<string, string> EnglishNames = new()
    {
        ["Pokémon Rote Edition"] = "Pokemon - Red Version",
        ["Pokémon Blaue Edition"] = "Pokemon - Blue Version",
        ["Pokémon Gelbe Edition"] = "Pokemon - Yellow Version",
        ["Pokémon Grüne Edition (JP)"] = "Pocket Monsters - Midori",
        ["Pokémon Goldene Edition"] = "Pokemon - Gold Version",
        ["Pokémon Silberne Edition"] = "Pokemon - Silver Version",
        ["Pokémon Kristall-Edition"] = "Pokemon - Crystal Version",
        ["Pokémon Pinball"] = "Pokemon Pinball",
        ["Pokémon Rubin-Edition"] = "Pokemon - Ruby Version",
        ["Pokémon Saphir-Edition"] = "Pokemon - Sapphire Version",
        ["Pokémon Feuerrote Edition"] = "Pokemon - FireRed Version",
        ["Pokémon Blattgrüne Edition"] = "Pokemon - LeafGreen Version",
        ["Pokémon Smaragd-Edition"] = "Pokemon - Emerald Version",
        ["Pokémon Pinball: Rubin & Saphir"] = "Pokemon Pinball - Ruby _ Sapphire",
        ["Pokémon Mystery Dungeon: Team Rot"] = "Pokemon Mystery Dungeon - Red Rescue Team",
        ["Pokémon Diamant-Edition"] = "Pokemon - Diamond Version",
        ["Pokémon Perl-Edition"] = "Pokemon - Pearl Version",
        ["Pokémon Platin-Edition"] = "Pokemon - Platinum Version",
        ["Pokémon HeartGold"] = "Pokemon - HeartGold Version",
        ["Pokémon SoulSilver"] = "Pokemon - SoulSilver Version",
        ["Pokémon Schwarze Edition"] = "Pokemon - Black Version",
        ["Pokémon Weiße Edition"] = "Pokemon - White Version",
        ["Pokémon Schwarze Edition 2"] = "Pokemon - Black Version 2",
        ["Pokémon Weiße Edition 2"] = "Pokemon - White Version 2",
        ["Pokémon Colosseum"] = "Pokemon Colosseum",
        ["Pokémon XD: Der dunkle Sturm"] = "Pokemon XD - Gale of Darkness",
        ["Pokémon Battle Revolution"] = "Pokemon Battle Revolution",
        ["Pokémon Stadium"] = "Pokemon Stadium",
        ["Pokémon Stadium 2"] = "Pokemon Stadium 2",
        ["Pokémon Snap"] = "Pokemon Snap",
        ["Pokémon Puzzle League"] = "Pokemon Puzzle League",
    };

    /// <summary>Repositories der Cover-Sammlung pro Konsole (GB-Spiele liegen teils bei GBC und umgekehrt).</summary>
    private static readonly Dictionary<Platform, string[]> Repos = new()
    {
        [Platform.PokemonMini] = ["Nintendo_-_Pokemon_Mini"],
        [Platform.GameBoy] = ["Nintendo_-_Game_Boy", "Nintendo_-_Game_Boy_Color"],
        [Platform.GameBoyColor] = ["Nintendo_-_Game_Boy_Color", "Nintendo_-_Game_Boy"],
        [Platform.GameBoyAdvance] = ["Nintendo_-_Game_Boy_Advance"],
        [Platform.Nintendo64] = ["Nintendo_-_Nintendo_64"],
        [Platform.NintendoDS] = ["Nintendo_-_Nintendo_DS"],
        [Platform.GameCube] = ["Nintendo_-_GameCube"],
        [Platform.Wii] = ["Nintendo_-_Wii"],
        [Platform.Nintendo3DS] = ["Nintendo_-_Nintendo_3DS"],
    };

    private static readonly HttpClient Http = CreateClient();
    private readonly Dictionary<string, Task<List<string>>> _indexes = [];
    private readonly SemaphoreSlim _downloads = new(4);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DexHub/0.1");
        return client;
    }

    private static string CacheFolder(string sub)
    {
        var dir = Path.Combine(AppSettings.AppDataFolder, "cache", sub);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Gibt den lokalen Pfad des Covers zurück oder null, wenn keins gefunden wurde.</summary>
    public async Task<string?> GetCoverPathAsync(GameEntry game, CancellationToken ct = default)
    {
        if (!Repos.TryGetValue(game.Info.Platform, out var repos)) return null;

        var key = EnglishNames.GetValueOrDefault(game.Title);
        foreach (var repo in repos)
        {
            List<string> index;
            try { index = await GetIndexAsync(repo); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                Console.WriteLine($"[DexHub] Cover-Verzeichnis {repo} nicht ladbar: {ex.Message}");
                continue;
            }

            var name = key is not null ? FindByKey(index, key) : FindByFileName(index, game.RomPath);
            if (name is null) continue;

            var file = Path.Combine(CacheFolder(Path.Combine("covers", repo)), name + ".png");
            if (File.Exists(file)) return file;

            await _downloads.WaitAsync(ct);
            try
            {
                var url = $"https://raw.githubusercontent.com/libretro-thumbnails/{repo}/master/Named_Boxarts/{Uri.EscapeDataString(name)}.png";
                var bytes = await Http.GetByteArrayAsync(url, ct);
                await File.WriteAllBytesAsync(file, bytes, ct);
                return file;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                Console.WriteLine($"[DexHub] Cover für {game.Title} nicht ladbar: {ex.Message}");
                return null;
            }
            finally
            {
                _downloads.Release();
            }
        }
        return null;
    }

    // ---------- Verzeichnis aller Cover einer Konsole ----------

    private Task<List<string>> GetIndexAsync(string repo)
    {
        lock (_indexes)
        {
            if (!_indexes.TryGetValue(repo, out var task) || task.IsFaulted)
                _indexes[repo] = task = LoadIndexAsync(repo);
            return task;
        }
    }

    /// <summary>Liste aller Cover-Namen, einmal über die GitHub-API geladen und dann lokal gespeichert.</summary>
    private static async Task<List<string>> LoadIndexAsync(string repo)
    {
        var cacheFile = Path.Combine(CacheFolder("cover-index"), repo + ".txt");
        if (File.Exists(cacheFile) && File.GetLastWriteTimeUtc(cacheFile) > DateTime.UtcNow.AddDays(-30))
            return [.. await File.ReadAllLinesAsync(cacheFile)];

        var url = $"https://api.github.com/repos/libretro-thumbnails/{repo}/git/trees/master?recursive=1";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));

        const string prefix = "Named_Boxarts/";
        var names = new List<string>();
        foreach (var entry in doc.RootElement.GetProperty("tree").EnumerateArray())
        {
            var path = entry.GetProperty("path").GetString() ?? "";
            if (path.StartsWith(prefix, StringComparison.Ordinal) && path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                names.Add(path[prefix.Length..^4]);
        }

        await File.WriteAllLinesAsync(cacheFile, names);
        return names;
    }

    // ---------- Passenden Namen finden ----------

    /// <summary>"Pokemon - Black Version" soll nicht "Pokemon - Black Version 2" treffen.</summary>
    private static string? FindByKey(List<string> index, string key) =>
        index.Where(n => n == key || n.StartsWith(key + " (", StringComparison.OrdinalIgnoreCase))
             .OrderBy(RegionScore)
             .FirstOrDefault();

    /// <summary>Für Spiele ohne Header-Erkennung (z. B. 3DS): über den Dateinamen.</summary>
    private static string? FindByFileName(List<string> index, string romPath)
    {
        var name = Path.GetFileNameWithoutExtension(romPath);
        var cut = name.IndexOfAny(['(', '[']);
        if (cut > 0) name = name[..cut];
        var wanted = Normalize(name);
        if (wanted.Length < 6) return null;

        return index.Where(n => Normalize(n).StartsWith(wanted, StringComparison.Ordinal))
                    .OrderBy(RegionScore)
                    .FirstOrDefault();
    }

    /// <summary>Deutsche bzw. europäische Cover bevorzugen, Demos/Betas vermeiden.</summary>
    private static int RegionScore(string name)
    {
        int score = name.Contains("Germany") ? 0
            : name.Contains("Europe") ? 1
            : name.Contains("USA") ? 2
            : 3;
        if (name.Contains("(Beta") || name.Contains("(Demo") || name.Contains("(Proto") || name.Contains("Kiosk"))
            score += 10;
        return score;
    }

    /// <summary>Nur Buchstaben und Ziffern, klein, ohne Akzente: "Pokémon_Platinum" -> "pokemonplatinum".</summary>
    private static string Normalize(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.Normalize(NormalizationForm.FormD))
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
