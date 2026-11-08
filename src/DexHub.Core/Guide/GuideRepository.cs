using DexHub.Core.Pokedex;

namespace DexHub.Core.Guide;

/// <summary>
/// Lädt die Guide-Daten aus dem PokéAPI-Datensatz (CSV-Dateien, einmalig heruntergeladen und lokal gespeichert).
/// </summary>
public static class GuideRepository
{
    private static readonly string[] Files =
    [
        "languages.csv", "types.csv", "type_names.csv", "type_efficacy.csv",
        "pokemon.csv", "pokemon_types.csv", "pokemon_types_past.csv", "pokemon_stats.csv",
        "pokemon_abilities.csv", "ability_names.csv", "ability_flavor_text.csv",
        "pokemon_species.csv", "pokemon_species_names.csv", "pokemon_species_flavor_text.csv",
        "pokemon_evolution.csv", "item_names.csv", "location_names.csv",
        "moves.csv", "move_names.csv", "move_flavor_text.csv",
        "version_groups.csv", "versions.csv", "version_names.csv",
        "pokemon_moves.csv",
    ];

    /// <param name="progress">Meldet Fortschritt als Text, z. B. "Lade pokemon_moves.csv (24/24)".</param>
    public static async Task<GuideData> LoadAsync(IProgress<string>? progress = null, bool forceRefresh = false)
    {
        var paths = new Dictionary<string, string>();
        for (int i = 0; i < Files.Length; i++)
        {
            progress?.Report($"Lade Guide-Daten: {Files[i]} ({i + 1}/{Files.Length})");
            paths[Files[i]] = await PokeApiRepository.GetCsvAsync(Files[i], forceRefresh);
        }
        progress?.Report("Bereite Guide-Daten auf…");
        return await Task.Run(() => Build(paths));
    }

    // ---------- Hilfen für robustes Lesen ----------

    private sealed class Table(string path)
    {
        private readonly Csv.Reader _file = Csv.Reader.Open(path);
        public IEnumerable<Row> Rows() => _file.Rows().Select(r => new Row(_file, r));
    }

    private sealed class Row(Csv.Reader file, string[] values)
    {
        public string S(string column)
        {
            int i = file.Col(column);
            return i >= 0 && i < values.Length ? values[i] : "";
        }

        public int I(string column) => int.TryParse(S(column), out var v) ? v : 0;
        public int? N(string column) => int.TryParse(S(column), out var v) ? v : null;
    }

    private static string Clean(string text) =>
        text.Replace("\n", " ").Replace("\f", " ").Replace("\r", " ").Replace("  ", " ").Trim();

    // ---------- Aufbau ----------

    private static GuideData Build(Dictionary<string, string> paths)
    {
        Table T(string name) => new(paths[name]);
        var data = new GuideData();

        int de = T("languages.csv").Rows().First(r => r.S("identifier") == "de").I("id");
        int en = T("languages.csv").Rows().First(r => r.S("identifier") == "en").I("id");

        // Typen
        var typeNames = T("type_names.csv").Rows().Where(r => r.I("local_language_id") == de)
            .ToDictionary(r => r.I("type_id"), r => r.S("name"));
        foreach (var r in T("types.csv").Rows())
        {
            int id = r.I("id");
            if (id is < 1 or > 18) continue; // ohne Crypto-/Stellar-Sondertypen
            data.Types[id] = new TypeInfo(id, r.S("identifier"), typeNames.GetValueOrDefault(id, r.S("identifier")));
        }
        foreach (var r in T("type_efficacy.csv").Rows())
            data.Efficacy[(r.I("damage_type_id"), r.I("target_type_id"))] = r.I("damage_factor");

        // Arten: Namen, Gattung, Pokédex-Text
        var namesDe = new Dictionary<int, (string Name, string Genus)>();
        var namesEn = new Dictionary<int, string>();
        foreach (var r in T("pokemon_species_names.csv").Rows())
        {
            int lang = r.I("local_language_id"), id = r.I("pokemon_species_id");
            if (lang == de) namesDe[id] = (r.S("name"), r.S("genus"));
            else if (lang == en) namesEn[id] = r.S("name");
        }

        foreach (var r in T("pokemon_species.csv").Rows())
        {
            int id = r.I("id");
            data.Pokemon[id] = new GuidePokemon
            {
                Id = id,
                Name = namesDe.TryGetValue(id, out var n) ? n.Name : namesEn.GetValueOrDefault(id, r.S("identifier")),
                NameEn = namesEn.GetValueOrDefault(id, r.S("identifier")),
                Genus = namesDe.TryGetValue(id, out var g) ? g.Genus : "",
                Generation = r.I("generation_id"),
                EvolvesFrom = r.I("evolves_from_species_id"),
                EvolutionChain = r.I("evolution_chain_id"),
                GenderRate = r.I("gender_rate"),
                CaptureRate = r.I("capture_rate"),
                BaseHappiness = r.I("base_happiness"),
                HatchCounter = r.I("hatch_counter"),
                GrowthRate = r.I("growth_rate_id"),
            };
        }

        // Neuester deutscher Pokédex-Text pro Art
        var bestFlavor = new Dictionary<int, int>();
        foreach (var r in T("pokemon_species_flavor_text.csv").Rows())
        {
            if (r.I("language_id") != de) continue;
            int id = r.I("species_id"), version = r.I("version_id");
            if (!data.Pokemon.TryGetValue(id, out var p) || bestFlavor.GetValueOrDefault(id) > version) continue;
            bestFlavor[id] = version;
            p.FlavorText = Clean(r.S("flavor_text"));
        }

        // Standardform jeder Art (pokemon.id == species_id) – Größe, Gewicht, Typen, Werte, Fähigkeiten
        foreach (var r in T("pokemon.csv").Rows())
        {
            if (r.I("is_default") != 1 || !data.Pokemon.TryGetValue(r.I("species_id"), out var p)) continue;
            p.Height = r.I("height");
            p.Weight = r.I("weight");
        }

        var types = new Dictionary<int, List<(int Slot, int Type)>>();
        foreach (var r in T("pokemon_types.csv").Rows())
        {
            int id = r.I("pokemon_id");
            if (!data.Pokemon.ContainsKey(id)) continue;
            if (!types.TryGetValue(id, out var list)) types[id] = list = [];
            list.Add((r.I("slot"), r.I("type_id")));
        }
        foreach (var (id, list) in types)
            data.Pokemon[id].Types = list.OrderBy(t => t.Slot).Select(t => t.Type).ToArray();

        var past = new Dictionary<(int Id, int Gen), List<(int Slot, int Type)>>();
        foreach (var r in T("pokemon_types_past.csv").Rows())
        {
            var key = (r.I("pokemon_id"), r.I("generation_id"));
            if (!data.Pokemon.ContainsKey(key.Item1)) continue;
            if (!past.TryGetValue(key, out var list)) past[key] = list = [];
            list.Add((r.I("slot"), r.I("type_id")));
        }
        foreach (var ((id, gen), list) in past)
            data.Pokemon[id].PastTypes.Add((gen, list.OrderBy(t => t.Slot).Select(t => t.Type).ToArray()));

        foreach (var r in T("pokemon_stats.csv").Rows())
        {
            if (!data.Pokemon.TryGetValue(r.I("pokemon_id"), out var p)) continue;
            int stat = r.I("stat_id") - 1;
            if (stat is < 0 or > 5) continue;
            p.BaseStats[stat] = r.I("base_stat");
            p.EvYield[stat] = r.I("effort");
        }

        foreach (var r in T("pokemon_abilities.csv").Rows().OrderBy(r => r.I("slot")))
            if (data.Pokemon.TryGetValue(r.I("pokemon_id"), out var p))
                p.Abilities.Add((r.I("ability_id"), r.I("is_hidden") == 1));

        // Fähigkeiten
        var abilityText = new Dictionary<int, (int Version, string Text)>();
        foreach (var r in T("ability_flavor_text.csv").Rows())
        {
            if (r.I("language_id") != de) continue;
            int id = r.I("ability_id"), vg = r.I("version_group_id");
            if (abilityText.TryGetValue(id, out var existing) && existing.Version > vg) continue;
            abilityText[id] = (vg, Clean(r.S("flavor_text")));
        }
        foreach (var r in T("ability_names.csv").Rows())
            if (r.I("local_language_id") == de)
            {
                int id = r.I("ability_id");
                data.Abilities[id] = new AbilityInfo(id, r.S("name"), abilityText.TryGetValue(id, out var t) ? t.Text : "");
            }

        // Attacken
        var moveNames = T("move_names.csv").Rows().Where(r => r.I("local_language_id") == de)
            .ToDictionary(r => r.I("move_id"), r => r.S("name"));
        var moveText = new Dictionary<int, (int Version, string Text)>();
        foreach (var r in T("move_flavor_text.csv").Rows())
        {
            if (r.I("language_id") != de) continue;
            int id = r.I("move_id"), vg = r.I("version_group_id");
            if (moveText.TryGetValue(id, out var existing) && existing.Version > vg) continue;
            moveText[id] = (vg, Clean(r.S("flavor_text")));
        }
        foreach (var r in T("moves.csv").Rows())
        {
            int id = r.I("id");
            if (id > 10000) continue; // Z-/Max-Attacken-Sonderfälle
            data.Moves[id] = new MoveInfo(id, moveNames.GetValueOrDefault(id, r.S("identifier")), r.I("type_id"),
                r.N("power"), r.N("accuracy"), r.N("pp"), r.I("priority"), r.I("damage_class_id"),
                r.I("generation_id"), moveText.TryGetValue(id, out var t) ? t.Text : "");
        }

        // Editionen
        var versionNames = T("version_names.csv").Rows().Where(r => r.I("local_language_id") == de)
            .ToDictionary(r => r.I("version_id"), r => r.S("name"));
        var versionsByGroup = T("versions.csv").Rows()
            .GroupBy(r => r.I("version_group_id"))
            .ToDictionary(g => g.Key, g => g.Select(r => versionNames.GetValueOrDefault(r.I("id"), r.S("identifier"))).ToList());
        foreach (var r in T("version_groups.csv").Rows())
        {
            int id = r.I("id");
            var name = versionsByGroup.TryGetValue(id, out var names) && names.Count > 0 ? string.Join(" / ", names) : r.S("identifier");
            data.VersionGroups.Add(new VersionGroupInfo(id, r.S("identifier"), name, r.I("generation_id"), r.I("order")));
        }

        // Entwicklungen
        var itemNames = T("item_names.csv").Rows().Where(r => r.I("local_language_id") == de)
            .ToDictionary(r => r.I("item_id"), r => r.S("name"));
        var locationNames = T("location_names.csv").Rows().Where(r => r.I("local_language_id") == de)
            .GroupBy(r => r.I("location_id")).ToDictionary(g => g.Key, g => g.First().S("name"));
        foreach (var r in T("pokemon_evolution.csv").Rows())
        {
            int to = r.I("evolved_species_id");
            if (!data.Pokemon.TryGetValue(to, out var target) || target.EvolvesFrom == 0) continue;
            data.Evolutions.Add(new EvolutionStep(target.EvolvesFrom, to, DescribeEvolution(r, data, itemNames, locationNames)));
        }

        // Attacken pro Pokémon (große Datei)
        foreach (var r in T("pokemon_moves.csv").Rows())
        {
            int id = r.I("pokemon_id");
            if (!data.Pokemon.ContainsKey(id)) continue;
            if (!data.Learnsets.TryGetValue(id, out var list)) data.Learnsets[id] = list = [];
            list.Add(new LearnEntry((short)r.I("version_group_id"), (short)r.I("move_id"),
                (byte)r.I("pokemon_move_method_id"), (byte)Math.Clamp(r.I("level"), 0, 255)));
        }

        return data;
    }

    private static string DescribeEvolution(Row r, GuideData data, Dictionary<int, string> items, Dictionary<int, string> locations)
    {
        var parts = new List<string>();
        string Item(int id) => items.GetValueOrDefault(id, $"Item #{id}");
        string Species(int id) => data.Pokemon.TryGetValue(id, out var p) ? p.Name : $"#{id}";

        if (r.I("minimum_level") > 0) parts.Add($"Lv. {r.I("minimum_level")}");
        if (r.I("trigger_item_id") > 0) parts.Add(Item(r.I("trigger_item_id")));
        if (r.I("held_item_id") > 0) parts.Add($"trägt {Item(r.I("held_item_id"))}");
        if (r.I("minimum_happiness") > 0) parts.Add("hohe Freundschaft");
        if (r.I("minimum_affection") > 0) parts.Add("hohe Zuneigung");
        if (r.I("minimum_beauty") > 0) parts.Add("hohe Schönheit");
        if (r.I("known_move_id") > 0 && data.Moves.TryGetValue(r.I("known_move_id"), out var move)) parts.Add($"kennt {move.Name}");
        if (r.I("known_move_type_id") > 0 && data.Types.TryGetValue(r.I("known_move_type_id"), out var kt)) parts.Add($"kennt {kt.Name}-Attacke");
        if (r.I("location_id") > 0) parts.Add($"in/bei {locations.GetValueOrDefault(r.I("location_id"), "bestimmtem Ort")}");
        if (r.I("party_species_id") > 0) parts.Add($"mit {Species(r.I("party_species_id"))} im Team");
        if (r.I("party_type_id") > 0 && data.Types.TryGetValue(r.I("party_type_id"), out var pt)) parts.Add($"mit {pt.Name}-Pokémon im Team");
        if (r.I("trade_species_id") > 0) parts.Add($"gegen {Species(r.I("trade_species_id"))}");
        if (r.I("gender_id") == 1) parts.Add("nur weiblich");
        if (r.I("gender_id") == 2) parts.Add("nur männlich");
        if (r.I("needs_overworld_rain") == 1) parts.Add("bei Regen");
        if (r.I("turn_upside_down") == 1) parts.Add("Konsole umdrehen");
        switch (r.S("time_of_day"))
        {
            case "day": parts.Add("tagsüber"); break;
            case "night": parts.Add("nachts"); break;
            case "dusk": parts.Add("in der Abenddämmerung"); break;
        }
        switch (r.S("relative_physical_stats"))
        {
            case "1": parts.Add("Angriff > Verteidigung"); break;
            case "-1": parts.Add("Angriff < Verteidigung"); break;
            case "0": parts.Add("Angriff = Verteidigung"); break;
        }

        string trigger = r.I("evolution_trigger_id") switch
        {
            1 => parts.Count == 0 ? "Level-Aufstieg" : "",
            2 => "Tausch",
            3 => "",
            4 => "Platz im Team + Pokéball",
            _ => parts.Count == 0 ? "besondere Bedingung" : "",
        };
        if (trigger.Length > 0) parts.Insert(0, trigger);
        return string.Join(", ", parts);
    }
}
