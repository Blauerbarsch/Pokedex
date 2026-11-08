using DexHub.Core.Settings;

namespace DexHub.Core.Pokedex;

/// <summary>
/// Lädt Pokédex-Daten aus dem offenen PokéAPI-Datensatz (https://pokeapi.co) und speichert sie lokal.
/// Statt über 1000 einzelne API-Anfragen laden wir drei CSV-Dateien aus dem PokéAPI-Repository –
/// das schont deren Server und geht viel schneller.
/// </summary>
public sealed class PokeApiRepository
{
    private const string CsvBase = "https://raw.githubusercontent.com/PokeAPI/pokeapi/master/data/v2/csv/";
    private const string SpriteBase = "https://raw.githubusercontent.com/PokeAPI/sprites/master/sprites/pokemon/";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DexHub/0.1");
        return client;
    }

    private static string CacheFolder
    {
        get
        {
            var dir = Path.Combine(AppSettings.AppDataFolder, "cache");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Alle Pokémon-Arten, sortiert nach Nummer.</summary>
    public async Task<List<PokemonSpecies>> LoadSpeciesAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        var speciesCsv = await GetCachedAsync("pokemon_species.csv", CsvBase + "pokemon_species.csv", forceRefresh, ct);
        var namesCsv = await GetCachedAsync("pokemon_species_names.csv", CsvBase + "pokemon_species_names.csv", forceRefresh, ct);
        var languagesCsv = await GetCachedAsync("languages.csv", CsvBase + "languages.csv", forceRefresh, ct);

        // Sprach-IDs über den Namen ermitteln statt fest einzutragen
        var languages = Csv.Parse(File.ReadAllText(languagesCsv));
        int LanguageId(string code) => int.Parse(languages.First(r => r["identifier"] == code)["id"]);
        int de = LanguageId("de"), en = LanguageId("en");

        var namesDe = new Dictionary<int, string>();
        var namesEn = new Dictionary<int, string>();
        foreach (var row in Csv.Parse(File.ReadAllText(namesCsv)))
        {
            int id = int.Parse(row["pokemon_species_id"]);
            int lang = int.Parse(row["local_language_id"]);
            if (lang == de) namesDe[id] = row["name"];
            else if (lang == en) namesEn[id] = row["name"];
        }

        var result = new List<PokemonSpecies>();
        foreach (var row in Csv.Parse(File.ReadAllText(speciesCsv)))
        {
            int id = int.Parse(row["id"]);
            int gen = int.Parse(row["generation_id"]);
            var en_ = namesEn.GetValueOrDefault(id, row["identifier"]);
            result.Add(new PokemonSpecies(id, namesDe.GetValueOrDefault(id, en_), en_, gen));
        }
        return result.OrderBy(s => s.Id).ToList();
    }

    /// <summary>Attacken, Wesen und Wachstumskurven – für die Anzeige einzelner Pokémon.</summary>
    public async Task<GameData> LoadGameDataAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        async Task<List<Dictionary<string, string>>> Table(string name) =>
            Csv.Parse(await File.ReadAllTextAsync(await GetCachedAsync(name, CsvBase + name, forceRefresh, ct), ct));

        var languages = await Table("languages.csv");
        int de = int.Parse(languages.First(r => r["identifier"] == "de")["id"]);

        var moves = new Dictionary<int, string>();
        foreach (var r in await Table("move_names.csv"))
            if (int.Parse(r["local_language_id"]) == de) moves[int.Parse(r["move_id"])] = r["name"];

        var natureNamesById = new Dictionary<int, string>();
        foreach (var r in await Table("nature_names.csv"))
            if (int.Parse(r["local_language_id"]) == de) natureNamesById[int.Parse(r["nature_id"])] = r["name"];
        var natures = new string[25];
        foreach (var r in await Table("natures.csv"))
        {
            int gameIndex = int.Parse(r["game_index"]);
            if (gameIndex is >= 0 and < 25)
                natures[gameIndex] = natureNamesById.GetValueOrDefault(int.Parse(r["id"]), r["identifier"]);
        }

        var growth = new Dictionary<int, int>();
        foreach (var r in await Table("pokemon_species.csv"))
            growth[int.Parse(r["id"])] = int.Parse(r["growth_rate_id"]);

        var expTables = new Dictionary<int, int[]>();
        foreach (var r in await Table("experience.csv"))
        {
            int rate = int.Parse(r["growth_rate_id"]), level = int.Parse(r["level"]);
            if (!expTables.TryGetValue(rate, out var table)) expTables[rate] = table = new int[101];
            if (level is >= 1 and <= 100) table[level] = int.Parse(r["experience"]);
        }

        return new GameData
        {
            MoveNames = moves,
            NatureNames = natures,
            GrowthRateBySpecies = growth,
            ExpTables = expTables,
        };
    }

    /// <summary>Lädt das Bild eines Pokémon (einmalig) und gibt den lokalen Pfad zurück.</summary>
    /// <summary>Höchstens 6 Bilder gleichzeitig laden – der Pokédex fragt beim Aufklappen viele auf einmal an.</summary>
    private static readonly SemaphoreSlim SpriteDownloads = new(6);

    public async Task<string?> GetSpritePathAsync(int id, CancellationToken ct = default, bool shiny = false)
    {
        var relative = Path.Combine(shiny ? "sprites-shiny" : "sprites", $"{id}.png");
        var local = Path.Combine(CacheFolder, relative);
        if (File.Exists(local)) return local;

        await SpriteDownloads.WaitAsync(ct);
        try
        {
            var url = shiny ? $"{SpriteBase}shiny/{id}.png" : $"{SpriteBase}{id}.png";
            return await GetCachedAsync(relative, url, false, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return null;
        }
        finally
        {
            SpriteDownloads.Release();
        }
    }

    /// <summary>Lädt eine CSV-Datei aus dem PokéAPI-Datensatz (einmalig) und gibt den lokalen Pfad zurück.</summary>
    public static Task<string> GetCsvAsync(string name, bool forceRefresh = false, CancellationToken ct = default) =>
        GetCachedAsync(name, CsvBase + name, forceRefresh, ct);

    private static async Task<string> GetCachedAsync(string fileName, string url, bool forceRefresh, CancellationToken ct)
    {
        var path = Path.Combine(CacheFolder, fileName);
        if (!forceRefresh && File.Exists(path)) return path;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = await Http.GetByteArrayAsync(url, ct);

        // Erst in Temp-Datei schreiben, damit bei Abbruch keine halbe Datei im Cache landet
        var tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes, ct);
        File.Move(tmp, path, overwrite: true);
        return path;
    }
}
