using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DexHub.Core.Guide;
using DexHub.Core.Models;

namespace DexHub.App;

/// <summary>
/// "Was fehlt mir noch?": Zu einem Pokémon zeigen, in welchen eigenen Spielen und wo man es bekommt –
/// wild, durch Entwicklung oder durch Zucht. Nutzt die Fundorte aus den Guide-Daten.
/// </summary>
public partial class MainWindow
{
    /// <summary>Art -> Editionen aus der eigenen Bibliothek, in denen man sie bekommen kann.</summary>
    private Dictionary<int, List<string>> _obtainable = [];

    /// <summary>Art -> Editionen (eigene), in denen sie wild vorkommt.</summary>
    private Dictionary<int, HashSet<int>> _wildInOwned = [];

    private sealed record OwnedVersion(int Version, int Generation, GameEntry Game);

    /// <summary>Eigene Editionen (jede nur einmal, auch wenn es mehrere ROMs davon gibt).</summary>
    private List<OwnedVersion> OwnedVersions() => _games
        .Where(g => g.Info.Version is not null && g.Info.Generation is not null)
        .GroupBy(g => g.Info.Version!.Value)
        .Select(g => new OwnedVersion(g.Key, g.First().Info.Generation!.Value, g.First()))
        .OrderBy(v => v.Version)
        .ToList();

    private void OnGuideLoaded()
    {
        RecomputeObtainable();
        ApplyDexFilter();
        if (_selectedDexId != 0) ShowWhereToFind(_selectedDexId);
    }

    private async void OnDexShowChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        ApplyDexFilter();
        if (DexShowBox.SelectedIndex == 3 && _guide is null)
        {
            DexCounter.Text = "Lade Fundorte…";
            await EnsureGuideLoadedAsync();
        }
    }

    private void RecomputeObtainable()
    {
        _obtainable = [];
        _wildInOwned = [];
        if (_guide is null) return;
        var versions = OwnedVersions();
        if (versions.Count == 0) return;

        var ids = versions.Select(v => v.Version).ToHashSet();
        foreach (var (species, list) in _guide.Encounters)
            foreach (var e in list)
                if (ids.Contains(e.Version))
                {
                    if (!_wildInOwned.TryGetValue(species, out var set)) _wildInOwned[species] = set = [];
                    set.Add(e.Version);
                }

        foreach (var species in _guide.Pokemon.Keys)
            foreach (var v in versions)
                if (HowToGet(species, v) is not null)
                {
                    if (!_obtainable.TryGetValue(species, out var list)) _obtainable[species] = list = [];
                    list.Add(v.Game.Title);
                }
    }

    private bool IsWild(int species, int version) =>
        _wildInOwned.TryGetValue(species, out var set) && set.Contains(version);

    /// <summary>
    /// Wie man eine Art in einer Edition bekommt: ("wild", Art), ("evolve", Vorstufe) oder ("breed", Elternart).
    /// null, wenn es dort (laut Daten) nicht geht.
    /// </summary>
    private (string Kind, int From)? HowToGet(int species, OwnedVersion v)
    {
        if (_guide is null || !_guide.Pokemon.TryGetValue(species, out var p)) return null;
        if (p.Generation > v.Generation) return null; // gab es damals noch nicht

        if (IsWild(species, v.Version)) return ("wild", species);

        // Vorstufe fangen und entwickeln
        int current = p.EvolvesFrom, guard = 0;
        while (current != 0 && guard++ < 5 && _guide.Pokemon.TryGetValue(current, out var pre))
        {
            if (IsWild(current, v.Version)) return ("evolve", current);
            current = pre.EvolvesFrom;
        }

        // Baby-Pokémon (z. B. Pichu) gibt es per Zucht aus einer späteren Stufe – ab Gen 2
        if (p.EvolvesFrom == 0 && v.Generation >= 2)
            foreach (var member in _guide.ChainOf(species))
                if (member != species && _guide.Pokemon.TryGetValue(member, out var m) && m.Generation <= v.Generation
                    && (IsWild(member, v.Version) || HowToGetEvolved(member, v)))
                    return ("breed", member);

        return null;
    }

    private bool HowToGetEvolved(int species, OwnedVersion v) =>
        HowToGet(species, v) is { Kind: "wild" or "evolve" };

    /// <summary>"Glumanda → Glutexo (Lv. 16) → Glurak (Lv. 36)"</summary>
    private string EvolutionPath(int from, int to)
    {
        var steps = new List<int>();
        int current = to, guard = 0;
        while (current != from && current != 0 && guard++ < 5)
        {
            steps.Insert(0, current);
            current = _guide!.Pokemon.TryGetValue(current, out var p) ? p.EvolvesFrom : 0;
        }

        var text = PokemonName(from);
        int previous = from;
        foreach (var s in steps)
        {
            var condition = _guide!.Evolutions.FirstOrDefault(e => e.From == previous && e.To == s)?.Condition;
            text += $" → {PokemonName(s)}" + (string.IsNullOrEmpty(condition) ? "" : $" ({condition})");
            previous = s;
        }
        return text;
    }

    private string PokemonName(int id) => _guide is not null && _guide.Pokemon.TryGetValue(id, out var p) ? p.Name : SpeciesName(id);

    // =====================================================================
    // Anzeige in der Pokédex-Detailansicht
    // =====================================================================

    private void ShowWhereToFind(int id)
    {
        WhereToFindPanel.Children.Clear();
        var versions = OwnedVersions();
        if (versions.Count == 0) return;

        bool caught = _dexById.TryGetValue(id, out var entry) && entry.IsCaught;
        WhereToFindPanel.Children.Add(Text(caught ? "🔎 Wo bekomme ich es (noch einmal)?" : "🔎 Wo bekomme ich es?", 16, bold: true));

        if (_guide is null)
        {
            if (_guideLoadTask is { IsCompleted: false } || GuideRepository.IsCached)
            {
                WhereToFindPanel.Children.Add(Text("Lade Fundorte…", 13, opacity: 0.7));
                _ = EnsureGuideLoadedAsync();
            }
            else
            {
                var button = new Button { Content = "Fundorte laden (einmalig ca. 30–40 MB)" };
                button.Click += async (_, _) =>
                {
                    button.IsEnabled = false;
                    button.Content = "Lade… (beim ersten Mal 1–2 Minuten)";
                    await EnsureGuideLoadedAsync();
                };
                WhereToFindPanel.Children.Add(button);
            }
            return;
        }

        int shown = 0;
        foreach (var v in versions)
        {
            var how = HowToGet(id, v);
            if (how is null) continue;
            shown++;

            var gamePanel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 0) };
            var play = LinkText("▶ spielen", () => _ = LaunchGameAsync(v.Game), 12);
            gamePanel.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children = { Text(v.Game.Title, 14, bold: true), play },
            });

            switch (how.Value.Kind)
            {
                case "wild":
                    foreach (var line in EncounterLines(id, v.Version, max: 4))
                        gamePanel.Children.Add(Text(line, 13));
                    break;
                case "evolve":
                    gamePanel.Children.Add(Text($"Entwicklung: {EvolutionPath(how.Value.From, id)}", 13));
                    foreach (var line in EncounterLines(how.Value.From, v.Version, max: 2))
                        gamePanel.Children.Add(Text($"{PokemonName(how.Value.From)}: {line.TrimStart('•', ' ')}", 13, opacity: 0.8));
                    break;
                case "breed":
                    gamePanel.Children.Add(Text($"Zucht: {PokemonName(how.Value.From)} in der Pension abgeben, aus dem Ei schlüpft {PokemonName(id)}.", 13));
                    break;
            }
            WhereToFindPanel.Children.Add(gamePanel);
        }

        if (shown == 0)
        {
            WhereToFindPanel.Children.Add(Text(
                "In deinen Spielen nicht wild zu finden – vermutlich nur als Geschenk, im Tausch, im Spiel-internen " +
                "Tausch oder bei einem Event. Tipp: Über „Tauschen“ von einem anderen Spielstand holen.", 13, opacity: 0.7));
        }
        else
        {
            WhereToFindPanel.Children.Add(Text("Geschenke und Tausch-Pokémon im Spiel sind hier nicht aufgeführt.", 11, opacity: 0.5));
        }
    }

    /// <summary>Kurze Fundort-Zeilen: "• Route 1 – Gras, Lv. 2–5, 30 %".</summary>
    private List<string> EncounterLines(int species, int version, int max)
    {
        var rows = (_guide!.Encounters.GetValueOrDefault(species) ?? [])
            .Where(e => e.Version == version)
            .GroupBy(e => (e.Location, e.Method, e.Conditions))
            .Select(g => (g.Key.Location, g.Key.Method, g.Key.Conditions,
                          Min: g.Min(e => e.MinLevel), Max: g.Max(e => e.MaxLevel), Rarity: Math.Min(100, g.Sum(e => e.Rarity))))
            .OrderByDescending(r => r.Rarity).ThenBy(r => r.Location)
            .ToList();

        var lines = rows.Take(max).Select(r =>
            $"• {r.Location} – {r.Method}, Lv. {(r.Min == r.Max ? r.Min.ToString() : $"{r.Min}–{r.Max}")}, {r.Rarity} %" +
            (r.Conditions.Length > 0 ? $" ({r.Conditions})" : "")).ToList();
        if (rows.Count > max) lines.Add($"  … und {rows.Count - max} weitere Orte (siehe Guide)");
        return lines;
    }
}
