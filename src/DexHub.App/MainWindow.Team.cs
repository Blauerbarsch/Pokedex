using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DexHub.Core.Pokemon;
using DexHub.Core.Settings;

namespace DexHub.App;

/// <summary>
/// Team-Planer im Guide: bis zu 6 eigene Pokémon (oder beliebige Arten) zusammenstellen und sehen,
/// gegen welche Typen das Team gut aufgestellt ist, wo es Lücken hat und was helfen würde.
/// Gerechnet wird mit der Typ-Tabelle der gewählten Edition; Fähigkeiten wie Schwebe zählen nicht mit.
/// </summary>
public partial class MainWindow
{
    private List<TeamMember> Team
    {
        get
        {
            if (!_settings.Teams.TryGetValue(_settings.CurrentProfile, out var team))
                _settings.Teams[_settings.CurrentProfile] = team = [];
            return team;
        }
    }

    /// <summary>Ein Team-Mitglied mit allem, was die Auswertung braucht.</summary>
    private sealed record Member(TeamMember Slot, int Species, string Name, int[] Types, int[] AttackTypes, OwnedPokemon? Owned);

    private List<OwnedPokemon> OwnedForTeam() => _collection
        .Where(p => !p.IsEgg && _guide!.Pokemon.ContainsKey(p.Species))
        .OrderByDescending(p => p.Level).ThenBy(p => p.Species)
        .ToList();

    /// <summary>Liste links: zuerst die eigenen Pokémon, dann alle Arten.</summary>
    private IEnumerable<GuideListItem> TeamCandidates(string query, int number)
    {
        bool Match(string name, int id) => query.Length == 0 || id == number || name.Contains(query, StringComparison.OrdinalIgnoreCase);
        var owned = OwnedForTeam();
        for (int i = 0; i < owned.Count; i++)
        {
            var p = owned[i];
            var name = NickOrName(p);
            if (Match(name, p.Species) || Match(SpeciesName(p.Species), p.Species))
                yield return new GuideListItem("teamown", i, $"★ Lv. {p.Level} {name} ({p.GameTitle})");
        }
        foreach (var p in _guide!.Pokemon.Values.OrderBy(p => p.Id))
            if (Match(p.Name, p.Id))
                yield return new GuideListItem("teamspecies", p.Id, $"#{p.Id:0000}  {p.Name}");
    }

    private void AddTeamMember(GuideListItem item)
    {
        if (Team.Count >= 6)
        {
            GuideStatus.Text = "Das Team ist voll (6 Pokémon). Erst eins entfernen.";
            return;
        }
        if (item.Kind == "teamown")
        {
            var owned = OwnedForTeam();
            if (item.Id < owned.Count) Team.Add(new TeamMember { Species = owned[item.Id].Species, Pid = owned[item.Id].Pid });
        }
        else Team.Add(new TeamMember { Species = item.Id });
        _settings.Save();
        GuideStatus.Text = "";
        Navigate("team", 0, remember: false);
    }

    private List<Member> ResolveTeam()
    {
        int gen = GuideGeneration;
        var result = new List<Member>();
        foreach (var slot in Team)
        {
            if (!_guide!.Pokemon.TryGetValue(slot.Species, out var info)) continue;
            var owned = slot.Pid == 0 ? null : _collection.FirstOrDefault(p => p.Pid == slot.Pid && p.Species == slot.Species);
            var types = info.TypesIn(gen);

            // Angriffstypen: Schadensattacken des eigenen Pokémon, sonst die eigenen Typen (Attacken gleichen Typs)
            var attackTypes = owned?.Moves
                .Where(m => m != 0 && _guide.Moves.TryGetValue(m, out var mv) && mv.DamageClass != 1 && (mv.Power ?? 0) > 1)
                .Select(m => _guide.Moves[m].Type).Distinct().ToArray() ?? [];
            if (attackTypes.Length == 0) attackTypes = types;

            result.Add(new Member(slot, slot.Species, owned is null ? info.Name : NickOrName(owned), types, attackTypes, owned));
        }
        return result;
    }

    private void ShowTeamPlanner()
    {
        var g = _guide!;
        int gen = GuideGeneration;
        var team = ResolveTeam();
        var allTypes = g.TypesIn(gen).Select(t => t.Id).ToList();

        GuideContent.Children.Add(Text("Team-Planer", 26, bold: true));
        GuideContent.Children.Add(Text(
            "Links Pokémon anklicken, um sie ins Team zu holen (★ = deine eigenen, mit ihren echten Attacken). " +
            $"Gerechnet wird mit der Typ-Tabelle von „{(GuideVersionBox.SelectedItem as DexHub.Core.Guide.VersionGroupInfo)?.Name}“. " +
            "Fähigkeiten (z. B. Schwebe) sind nicht berücksichtigt.", 13, opacity: 0.75));

        // Mitglieder
        var members = new WrapPanel();
        foreach (var m in team) members.Children.Add(MemberCard(m));
        if (team.Count == 0) members.Children.Add(Text("Noch leer.", opacity: 0.6));
        GuideContent.Children.Add(Card($"Dein Team ({team.Count}/6)", members, collapsible: false));
        if (team.Count == 0) return;

        // Abwehr: wie viele sind schwach / resistent gegen jeden Angriffstyp
        var defense = allTypes.Select(t =>
        {
            var factors = team.Select(m => g.Effectiveness(t, m.Types, gen)).ToList();
            return (Type: t, Weak: factors.Count(f => f > 100), Resist: factors.Count(f => f is > 0 and < 100), Immune: factors.Count(f => f == 0));
        }).ToList();

        var defenseGrid = new StackPanel { Spacing = 3 };
        defenseGrid.Children.Add(Text("Angriffstyp → wie viele deiner Pokémon sind schwach (rot) oder halten gut aus (grün).", 12, opacity: 0.7));
        foreach (var d in defense)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new Border { Width = 100, Child = TypeBadge(d.Type, 12) });
            row.Children.Add(Bar(d.Weak, "#E3350D", $"{d.Weak} schwach"));
            row.Children.Add(Bar(d.Resist + d.Immune, "#4CAF50", $"{d.Resist + d.Immune} resistent" + (d.Immune > 0 ? $" ({d.Immune} immun)" : "")));
            if (d.Weak >= 3 || (d.Weak >= 2 && d.Resist + d.Immune == 0))
                row.Children.Add(Text("⚠", 14));
            defenseGrid.Children.Add(row);
        }
        GuideContent.Children.Add(Card("Abwehr", defenseGrid));

        // Angriff: welche Typen erwischt das Team sehr effektiv?
        var attackTypes = team.SelectMany(m => m.AttackTypes).Distinct().ToList();
        int Best(int defender) => attackTypes.Count == 0 ? 100 : attackTypes.Max(a => g.Effectiveness(a, defender, gen));
        var uncovered = allTypes.Where(t => Best(t) <= 100).ToList();
        var walls = allTypes.Where(t => Best(t) < 100).ToList();

        var attack = new StackPanel { Spacing = 6 };
        attack.Children.Add(Wrap([Text("Eure Angriffstypen:  ", 13, opacity: 0.8), .. attackTypes.Select(t => TypeBadge(t, 12))]));
        attack.Children.Add(uncovered.Count == 0
            ? Text("✓ Gegen jeden Typ habt ihr eine sehr effektive Attacke.", 13)
            : Wrap([Text("Keine sehr effektive Attacke gegen:  ", 13), .. uncovered.Select(t => TypeBadge(t, 12))]));
        if (walls.Count > 0)
            attack.Children.Add(Wrap([Text("Nur schwacher Schaden gegen:  ", 13, opacity: 0.8), .. walls.Select(t => TypeBadge(t, 12))]));
        GuideContent.Children.Add(Card("Angriff", attack));

        // Vorschläge
        var dangerous = defense.Where(d => d.Weak >= 2 && d.Weak > d.Resist + d.Immune).Select(d => d.Type).ToList();
        GuideContent.Children.Add(Card("Vorschläge", Suggestions(team, uncovered, dangerous, allTypes, gen)));
    }

    private Control Suggestions(List<Member> team, List<int> uncovered, List<int> dangerous, List<int> allTypes, int gen)
    {
        var g = _guide!;
        var panel = new StackPanel { Spacing = 6 };
        if (uncovered.Count == 0 && dangerous.Count == 0)
        {
            panel.Children.Add(Text("Das Team ist gut ausbalanciert. 👍", 13));
            return panel;
        }

        if (dangerous.Count > 0)
            panel.Children.Add(Wrap([Text("Gefährlich für euch:  ", 13), .. dangerous.Select(t => TypeBadge(t, 12))]));

        // Welcher Angriffstyp würde die meisten Lücken schließen?
        var typeIdeas = allTypes
            .Select(t => (Type: t, Covers: uncovered.Where(u => g.Effectiveness(t, u, gen) > 100).ToList()))
            .Where(x => x.Covers.Count > 0)
            .OrderByDescending(x => x.Covers.Count).Take(3).ToList();
        foreach (var (type, covers) in typeIdeas)
            panel.Children.Add(Wrap([Text("Eine Attacke vom Typ ", 13), TypeBadge(type, 12),
                Text($" träfe sehr effektiv: {string.Join(", ", covers.Select(c => g.Types[c].Name))}", 13)]));

        // Eigene Pokémon, die das Team am meisten ergänzen würden
        if (team.Count < 6)
        {
            var inTeam = team.Select(m => m.Slot.Pid).Where(p => p != 0).ToHashSet();
            var candidates = OwnedForTeam()
                .Where(p => !inTeam.Contains(p.Pid))
                .GroupBy(p => p.Species).Select(grp => grp.First()) // je Art das stärkste
                .Select(p =>
                {
                    var info = g.Pokemon[p.Species];
                    var types = info.TypesIn(gen);
                    var moves = p.Moves.Where(m => m != 0 && g.Moves.TryGetValue(m, out var mv) && mv.DamageClass != 1 && (mv.Power ?? 0) > 1)
                                       .Select(m => g.Moves[m].Type).Distinct().ToArray();
                    if (moves.Length == 0) moves = types;
                    int covers = uncovered.Count(u => moves.Any(a => g.Effectiveness(a, u, gen) > 100));
                    int resists = dangerous.Count(d => g.Effectiveness(d, types, gen) < 100);
                    return (Pokemon: p, Score: covers * 2 + resists, Covers: covers, Resists: resists);
                })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score).ThenByDescending(x => x.Pokemon.Level)
                .Take(5).ToList();

            if (candidates.Count > 0)
            {
                panel.Children.Add(Text("Passend aus deinen Pokémon:", 13, bold: true));
                foreach (var c in candidates)
                {
                    var reasons = new List<string>();
                    if (c.Covers > 0) reasons.Add($"schließt {c.Covers} Angriffslücke(n)");
                    if (c.Resists > 0) reasons.Add($"hält {c.Resists} gefährliche(n) Typ(en) aus");
                    var p = c.Pokemon;
                    var link = LinkText($"＋ Lv. {p.Level} {NickOrName(p)} ({p.GameTitle}) – {string.Join(", ", reasons)}", () =>
                    {
                        if (Team.Count >= 6) return;
                        Team.Add(new TeamMember { Species = p.Species, Pid = p.Pid });
                        _settings.Save();
                        Navigate("team", 0, remember: false);
                    }, 13);
                    panel.Children.Add(link);
                }
            }
        }
        return panel;
    }

    private static Control Bar(int count, string color, string tip)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Width = 110 };
        for (int i = 0; i < count; i++)
            bar.Children.Add(new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.Parse(color)) });
        if (count == 0) bar.Children.Add(Text("–", 12, opacity: 0.4));
        ToolTip.SetTip(bar, tip);
        return bar;
    }

    private Border MemberCard(Member m)
    {
        var g = _guide!;
        var sprite = new Image { Width = 64, Height = 64 };
        RenderOptions.SetBitmapInterpolationMode(sprite, BitmapInterpolationMode.None);
        if (m.Owned is not null) _ = LoadOwnedSpriteAsync(m.Owned, sprite); else _ = SetSpriteAsync(sprite, m.Species);

        var remove = new Button { Content = "✕", Padding = new Thickness(6, 0), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
        remove.Click += (_, _) =>
        {
            Team.Remove(m.Slot);
            _settings.Save();
            Navigate("team", 0, remember: false);
        };

        var info = new StackPanel { Spacing = 3 };
        info.Children.Add(Text(m.Name, 14, bold: true));
        info.Children.Add(Text(m.Owned is null ? "Art (nicht aus deinen Spielständen)" : $"Lv. {m.Owned.Level} · {m.Owned.GameTitle}", 11, opacity: 0.6));
        info.Children.Add(Wrap(m.Types.Select(t => TypeBadge(t, 11))));
        if (m.Owned is not null)
            foreach (var move in m.Owned.Moves.Where(x => x != 0))
                if (g.Moves.TryGetValue(move, out var mv))
                    info.Children.Add(Wrap([TypeBadge(mv.Type, 10), Text($" {mv.Name}" + (mv.Power is > 1 ? $" ({mv.Power})" : ""), 12)]));

        var card = new Border
        {
            Background = TileBrush,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 8, 8),
            Width = 220,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Child = new StackPanel
            {
                Children =
                {
                    remove,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { sprite, info } },
                },
            },
        };
        ToolTip.SetTip(card, "Klick: im Guide öffnen");
        sprite.PointerPressed += (_, _) => Navigate("pokemon", m.Species);
        return card;
    }
}
