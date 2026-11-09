using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DexHub.Core.Guide;

namespace DexHub.App;

/// <summary>
/// Der Guide: Pokémon (Typen, Schwächen, Werte, Fähigkeiten, Entwicklungen, Attacken), Typen und Attacken.
/// Alles ist verlinkt; "Zurück" führt zur vorigen Seite.
/// </summary>
public partial class MainWindow
{
    private GuideData? _guide;
    private bool _guideLoading;
    private string _guideMode = "pokemon";
    private readonly Stack<(string Kind, int Id)> _guideHistory = new();
    private (string Kind, int Id)? _guideCurrent;
    private bool _guideListUpdating;

    /// <summary>Eintrag in der Liste links.</summary>
    private sealed record GuideListItem(string Kind, int Id, string Label)
    {
        public override string ToString() => Label;
    }

    private int GuideGeneration =>
        GuideVersionBox.SelectedItem is VersionGroupInfo vg ? vg.Generation : 9;

    private int GuideVersionGroup =>
        GuideVersionBox.SelectedItem is VersionGroupInfo vg ? vg.Id : 0;

    // =====================================================================
    // Laden
    // =====================================================================

    private async void OnMainTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || e.Source != MainTabs) return;
        if (MainTabs.SelectedItem == GuideTab) await EnsureGuideLoadedAsync();
        if (MainTabs.SelectedItem == SavesTab) BuildSavesPanel();
    }

    private Task? _guideLoadTask;

    /// <summary>Lädt die Guide-Daten einmal; weitere Aufrufe warten auf denselben Ladevorgang.</summary>
    private Task EnsureGuideLoadedAsync()
    {
        if (_guide is not null) return Task.CompletedTask;
        if (_guideLoadTask is { IsCompleted: false }) return _guideLoadTask;
        return _guideLoadTask = LoadGuideAsync();
    }

    private async Task LoadGuideAsync()
    {
        _guideLoading = true;
        GuideContent.Children.Clear();
        GuideContent.Children.Add(Text("Beim ersten Öffnen lädt der Guide seine Daten (einmalig, ca. 30–40 MB). " +
                                       "Das kann ein bis zwei Minuten dauern.", opacity: 0.8));
        try
        {
            var progress = new Progress<string>(msg => GuideStatus.Text = msg);
            _guide = await GuideRepository.LoadAsync(progress);
            GuideStatus.Text = "";

            var groups = _guide.VersionGroups.OrderBy(v => v.Order).ToList();
            GuideVersionBox.ItemsSource = groups;
            GuideVersionBox.SelectedItem = PreferredVersionGroup(groups) ?? groups.LastOrDefault();

            ShowGuideList();
            Navigate("chart", 0, remember: false);
            OnGuideLoaded();
        }
        catch (Exception ex)
        {
            GuideStatus.Text = "";
            GuideContent.Children.Clear();
            GuideContent.Children.Add(Text("Guide-Daten konnten nicht geladen werden: " + ex.Message));
            Console.WriteLine("[DexHub] Guide: " + ex);
        }
        finally
        {
            _guideLoading = false;
        }
    }

    /// <summary>Standard-Edition: das Spiel aus der Bibliothek mit der höchsten Generation.</summary>
    private VersionGroupInfo? PreferredVersionGroup(List<VersionGroupInfo> groups)
    {
        var byTitle = new Dictionary<string, string>
        {
            ["Pokémon Rote Edition"] = "red-blue", ["Pokémon Blaue Edition"] = "red-blue",
            ["Pokémon Gelbe Edition"] = "yellow", ["Pokémon Goldene Edition"] = "gold-silver",
            ["Pokémon Silberne Edition"] = "gold-silver", ["Pokémon Kristall-Edition"] = "crystal",
            ["Pokémon Rubin-Edition"] = "ruby-sapphire", ["Pokémon Saphir-Edition"] = "ruby-sapphire",
            ["Pokémon Smaragd-Edition"] = "emerald", ["Pokémon Feuerrote Edition"] = "firered-leafgreen",
            ["Pokémon Blattgrüne Edition"] = "firered-leafgreen", ["Pokémon Diamant-Edition"] = "diamond-pearl",
            ["Pokémon Perl-Edition"] = "diamond-pearl", ["Pokémon Platin-Edition"] = "platinum",
            ["Pokémon HeartGold"] = "heartgold-soulsilver", ["Pokémon SoulSilver"] = "heartgold-soulsilver",
            ["Pokémon Schwarze Edition"] = "black-white", ["Pokémon Weiße Edition"] = "black-white",
            ["Pokémon Schwarze Edition 2"] = "black-2-white-2", ["Pokémon Weiße Edition 2"] = "black-2-white-2",
        };
        var identifier = _games
            .Where(g => byTitle.ContainsKey(g.Title))
            .OrderByDescending(g => g.SavePath is not null)
            .ThenByDescending(g => g.Info.Generation ?? 0)
            .Select(g => byTitle[g.Title])
            .FirstOrDefault();
        return groups.FirstOrDefault(g => g.Identifier == identifier);
    }

    // =====================================================================
    // Navigation
    // =====================================================================

    private void OnGuideModeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string mode }) SetGuideMode(mode);
    }

    private void SetGuideMode(string mode)
    {
        _guideMode = mode;
        GuideSearchBox.Text = "";
        ShowGuideList();
        if (mode == "types") Navigate("chart", 0);
        if (mode == "team") Navigate("team", 0);
    }

    private void OnGuideSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (_ready) ShowGuideList();
    }

    private void OnGuideVersionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_guideCurrent is { } current) Navigate(current.Kind, current.Id, remember: false);
    }

    private void OnGuideBackClick(object? sender, RoutedEventArgs e)
    {
        if (_guideHistory.Count == 0) return;
        var (kind, id) = _guideHistory.Pop();
        Navigate(kind, id, remember: false);
    }

    private void OnGuideListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_guideListUpdating || GuideList.SelectedItem is not GuideListItem item) return;
        if (item.Kind is "teamown" or "teamspecies")
        {
            AddTeamMember(item);
            return;
        }
        Navigate(item.Kind, item.Id);
    }

    private async void OnOpenInGuideClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedDexId == 0) return;
        MainTabs.SelectedItem = GuideTab;
        await EnsureGuideLoadedAsync();
        if (_guide is null) return;
        _guideMode = "pokemon";
        ShowGuideList();
        Navigate("pokemon", _selectedDexId);
    }

    /// <summary>Liste links: je nach Modus Pokémon, Typen oder Attacken – gefiltert nach der Suche.</summary>
    private void ShowGuideList()
    {
        if (_guide is null) return;
        var query = GuideSearchBox.Text?.Trim() ?? "";
        int number = int.TryParse(query.TrimStart('#'), out var n) ? n : -1;
        bool Match(string name, int id) =>
            query.Length == 0 || id == number || name.Contains(query, StringComparison.OrdinalIgnoreCase);

        IEnumerable<GuideListItem> items = _guideMode switch
        {
            "types" => new[] { new GuideListItem("chart", 0, "📊 Typ-Tabelle") }.Concat(
                _guide.TypesIn(GuideGeneration).Where(t => Match(t.Name, t.Id))
                      .Select(t => new GuideListItem("type", t.Id, t.Name))),
            "team" => TeamCandidates(query, number),
            "moves" => _guide.Moves.Values.Where(m => Match(m.Name, m.Id)).OrderBy(m => m.Name)
                             .Select(m => new GuideListItem("move", m.Id, m.Name)),
            _ => _guide.Pokemon.Values.Where(p => Match(p.Name, p.Id) || p.NameEn.Contains(query, StringComparison.OrdinalIgnoreCase))
                       .OrderBy(p => p.Id)
                       .Select(p => new GuideListItem("pokemon", p.Id, $"#{p.Id:0000}  {p.Name}")),
        };

        _guideListUpdating = true;
        GuideList.ItemsSource = items.ToList();
        _guideListUpdating = false;
    }

    private void Navigate(string kind, int id, bool remember = true)
    {
        if (_guide is null) return;
        if (remember && _guideCurrent is { } current && current != (kind, id)) _guideHistory.Push(current);
        _guideCurrent = (kind, id);
        GuideBackButton.IsEnabled = _guideHistory.Count > 0;

        GuideContent.Children.Clear();
        switch (kind)
        {
            case "pokemon": ShowGuidePokemon(id); break;
            case "type": ShowGuideType(id); break;
            case "move": ShowGuideMove(id); break;
            case "team": ShowTeamPlanner(); break;
            default: ShowTypeChart(); break;
        }
        GuideScroll.Offset = new Vector(0, 0);
    }

    // =====================================================================
    // Bausteine
    // =====================================================================

    private Control TypeBadge(int typeId, double fontSize = 13)
    {
        if (_guide is null || !_guide.Types.TryGetValue(typeId, out var type)) return new TextBlock();
        var color = Color.Parse(GuideData.TypeColors.GetValueOrDefault(type.Identifier, "#888888"));
        var badge = new Border
        {
            Background = new SolidColorBrush(color),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 2),
            Margin = new Thickness(0, 2, 4, 2),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock { Text = type.Name, Foreground = Brushes.White, FontWeight = FontWeight.SemiBold, FontSize = fontSize },
        };
        ToolTip.SetTip(badge, $"Typ {type.Name} öffnen");
        badge.PointerPressed += (_, _) => Navigate("type", typeId);
        return badge;
    }

    private Control LinkText(string text, Action onClick, double size = 14)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = new SolidColorBrush(Color.Parse("#4FA3FF")),
            Cursor = new Cursor(StandardCursorType.Hand),
            TextDecorations = TextDecorations.Underline,
            VerticalAlignment = VerticalAlignment.Center,
        };
        block.PointerPressed += (_, _) => onClick();
        return block;
    }

    private static Border Card(string title, Control content, bool collapsible = true)
    {
        Control body = collapsible
            ? new Expander { Header = title, Content = content, IsExpanded = true, HorizontalAlignment = HorizontalAlignment.Stretch }
            : new StackPanel { Spacing = 6, Children = { Text(title, 16, bold: true), content } };
        return new Border { Child = body };
    }

    private static WrapPanel Wrap(IEnumerable<Control> children)
    {
        var wrap = new WrapPanel();
        foreach (var c in children) wrap.Children.Add(c);
        return wrap;
    }

    private async Task SetSpriteAsync(Image image, int id)
    {
        var path = await _pokeApi.GetSpritePathAsync(id);
        if (path is null) return;
        try { image.Source = new Bitmap(path); } catch { /* defektes Bild ignorieren */ }
    }

    // =====================================================================
    // Seite: Pokémon
    // =====================================================================

    private void ShowGuidePokemon(int id)
    {
        var g = _guide!;
        if (!g.Pokemon.TryGetValue(id, out var p)) return;
        int gen = GuideGeneration;
        var types = p.TypesIn(gen);

        // Kopf
        var sprite = new Image { Width = 128, Height = 128 };
        RenderOptions.SetBitmapInterpolationMode(sprite, BitmapInterpolationMode.None);
        _ = SetSpriteAsync(sprite, id);
        var head = new StackPanel
        {
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Text($"#{p.Id:0000}  {p.Name}", 26, bold: true),
                Text($"{p.Genus} · Englisch: {p.NameEn} · eingeführt in Gen {p.Generation}", opacity: 0.7),
                Wrap(types.Select(t => TypeBadge(t, 15))),
                Text(p.FlavorText, opacity: 0.9),
            },
        };
        if (p.Generation > gen)
            head.Children.Add(Text($"⚠ Dieses Pokémon gibt es in der gewählten Edition noch nicht.", opacity: 0.8));
        GuideContent.Children.Add(new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("140,*"),
            Children = { sprite, WithColumn(head, 1) },
        });

        // Schwächen und Resistenzen
        var defense = new StackPanel { Spacing = 4 };
        var byFactor = g.TypesIn(gen)
            .Select(t => (Type: t.Id, Factor: g.Effectiveness(t.Id, types, gen)))
            .GroupBy(x => x.Factor)
            .ToDictionary(x => x.Key, x => x.Select(v => v.Type).ToList());
        foreach (var (factor, label) in new[] { (400, "4× – sehr anfällig"), (200, "2× – anfällig"), (50, "½× – resistent"), (25, "¼× – sehr resistent"), (0, "0× – immun") })
        {
            if (!byFactor.TryGetValue(factor, out var list) || list.Count == 0) continue;
            defense.Children.Add(new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("170,*"),
                Children = { Text(label, bold: true), WithColumn(Wrap(list.Select(t => TypeBadge(t))), 1) },
            });
        }
        defense.Children.Add(Text("Effektive Attacken gegen dieses Pokémon sind die Typen bei „anfällig“.", 12, opacity: 0.6));
        GuideContent.Children.Add(Card("Schwächen & Resistenzen", defense));

        // Offensiv: wogegen sind die eigenen Typen stark?
        var offense = new StackPanel { Spacing = 4 };
        foreach (var own in types)
        {
            var strong = g.TypesIn(gen).Where(t => g.Effectiveness(own, t.Id, gen) == 200).Select(t => t.Id).ToList();
            var weak = g.TypesIn(gen).Where(t => g.Effectiveness(own, t.Id, gen) is > 0 and < 100).Select(t => t.Id).ToList();
            var none = g.TypesIn(gen).Where(t => g.Effectiveness(own, t.Id, gen) == 0).Select(t => t.Id).ToList();
            offense.Children.Add(new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    Wrap([TypeBadge(own), Text("-Attacken sind …", opacity: 0.8)]),
                    Row("sehr effektiv gegen", strong),
                    Row("nicht sehr effektiv gegen", weak),
                    Row("wirkungslos gegen", none),
                },
            });
        }
        GuideContent.Children.Add(Card("Eigene Typen im Angriff (STAB)", offense));

        // Basiswerte
        var stats = new StackPanel { Spacing = 4 };
        for (int i = 0; i < 6; i++)
        {
            int v = p.BaseStats[i];
            var color = v >= 120 ? "#2EB872" : v >= 90 ? "#8BC34A" : v >= 60 ? "#F7D02C" : "#EE8130";
            stats.Children.Add(new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("130,50,*"),
                Children =
                {
                    Text(GuideData.StatNames[i]),
                    WithColumn(Text(v.ToString(), bold: true), 1),
                    WithColumn(new ProgressBar
                    {
                        Minimum = 0, Maximum = 255, Value = v, Height = 12,
                        Foreground = new SolidColorBrush(Color.Parse(color)),
                        VerticalAlignment = VerticalAlignment.Center,
                    }, 2),
                },
            });
        }
        stats.Children.Add(Text($"Summe: {p.BaseStatTotal}", bold: true));
        var evs = Enumerable.Range(0, 6).Where(i => p.EvYield[i] > 0).Select(i => $"{p.EvYield[i]} {GuideData.StatNames[i]}");
        stats.Children.Add(Text("Fleißpunkte beim Besiegen: " + string.Join(", ", evs), 12, opacity: 0.7));
        GuideContent.Children.Add(Card("Basiswerte", stats));

        // Fähigkeiten
        if (p.Abilities.Count > 0)
        {
            var abilities = new StackPanel { Spacing = 6 };
            foreach (var (abilityId, hidden) in p.Abilities)
            {
                if (!g.Abilities.TryGetValue(abilityId, out var a)) continue;
                abilities.Children.Add(new StackPanel
                {
                    Children =
                    {
                        Text(a.Name + (hidden ? "  (versteckte Fähigkeit)" : ""), bold: true),
                        Text(a.Description, 13, opacity: 0.8),
                    },
                });
            }
            if (gen < 3) abilities.Children.Add(Text("Hinweis: Fähigkeiten gibt es erst ab Gen 3.", 12, opacity: 0.6));
            GuideContent.Children.Add(Card("Fähigkeiten", abilities));
        }

        // Entwicklung
        var chain = g.ChainOf(id);
        var steps = g.Evolutions.Where(e => chain.Contains(e.To)).OrderBy(e => chain.IndexOf(e.To)).ToList();
        var evolution = new StackPanel { Spacing = 6 };
        if (steps.Count == 0)
        {
            evolution.Children.Add(Text("Entwickelt sich nicht.", opacity: 0.7));
        }
        else
        {
            foreach (var step in steps)
            {
                evolution.Children.Add(Wrap(
                [
                    PokemonChip(step.From),
                    Text("  →  ", 16, bold: true),
                    PokemonChip(step.To),
                    Text("   " + step.Condition, opacity: 0.85),
                ]));
            }
        }
        GuideContent.Children.Add(Card("Entwicklung", evolution));

        // Fundorte in der gewählten Edition
        GuideContent.Children.Add(Card($"Fundorte in {(GuideVersionBox.SelectedItem as VersionGroupInfo)?.Name}", BuildEncounters(id)));

        // Zucht und Daten
        string gender = p.GenderRate < 0 ? "geschlechtslos"
            : $"♂ {100 - p.GenderRate * 12.5:0.#} % · ♀ {p.GenderRate * 12.5:0.#} %";
        string[] growth = ["", "Langsam", "Mittel", "Schnell", "Mittel-langsam", "Unregelmäßig", "Schwankend"];
        var info = new StackPanel
        {
            Spacing = 3,
            Children =
            {
                Text($"Geschlecht: {gender}"),
                Text($"Fangrate: {p.CaptureRate} (von 255 – höher = leichter)"),
                Text($"Basis-Freundschaft: {p.BaseHappiness}"),
                Text($"Schlüpfen: ca. {(p.HatchCounter + 1) * 255} Schritte"),
                Text($"Wachstum: {growth.ElementAtOrDefault(p.GrowthRate) ?? "?"}"),
                Text($"Größe: {p.Height / 10.0:0.0} m · Gewicht: {p.Weight / 10.0:0.0} kg"),
            },
        };
        GuideContent.Children.Add(Card("Zucht & Daten", info));

        // Attacken der gewählten Edition
        GuideContent.Children.Add(Card($"Attacken in {(GuideVersionBox.SelectedItem as VersionGroupInfo)?.Name}", BuildLearnset(id)));

        StackPanel Row(string label, List<int> typeIds) => new()
        {
            Orientation = Orientation.Horizontal,
            Children = { Text($"   {label}: ", 13, opacity: 0.8), typeIds.Count == 0 ? (Control)Text("–", 13) : Wrap(typeIds.Select(t => TypeBadge(t, 12))) },
        };
    }

    /// <summary>Wilde Begegnungen, zusammengefasst nach Edition, Ort, Methode und Bedingung.</summary>
    private Control BuildEncounters(int id)
    {
        var g = _guide!;
        var panel = new StackPanel { Spacing = 4 };
        var versions = g.VersionsByGroup.GetValueOrDefault(GuideVersionGroup) ?? [];
        var versionIds = versions.Select(v => v.Id).ToHashSet();
        var encounters = g.Encounters.GetValueOrDefault(id)?.Where(e => versionIds.Contains(e.Version)).ToList() ?? [];

        if (encounters.Count == 0)
        {
            panel.Children.Add(Text("In dieser Edition nicht wild zu finden – evtl. nur durch Entwicklung, Zucht, Tausch, " +
                                    "Geschenk oder Event. (Für neuere Editionen fehlen teils Daten.)", opacity: 0.7));
            return panel;
        }

        bool showVersion = versions.Count > 1;
        var rows = encounters
            .GroupBy(e => (e.Version, e.Location, e.Method, e.Conditions))
            .Select(x => (x.Key.Version, x.Key.Location, x.Key.Method, x.Key.Conditions,
                          Min: x.Min(e => e.MinLevel), Max: x.Max(e => e.MaxLevel), Rarity: x.Sum(e => e.Rarity)))
            .OrderBy(x => x.Location).ThenBy(x => x.Method).ThenBy(x => x.Version)
            .ToList();

        Grid Row(string version, string location, string method, string level, string chance, string condition, bool header = false)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(showVersion ? "110,*,150,80,70,200" : "0,*,150,80,70,200") };
            TextBlock Cell(string t) => Text(t, 13, bold: header, opacity: header ? 0.7 : 1);
            grid.Children.Add(Cell(version));
            grid.Children.Add(WithColumn(Cell(location), 1));
            grid.Children.Add(WithColumn(Cell(method), 2));
            grid.Children.Add(WithColumn(Cell(level), 3));
            grid.Children.Add(WithColumn(Cell(chance), 4));
            grid.Children.Add(WithColumn(Cell(condition), 5));
            return grid;
        }

        panel.Children.Add(Row("Edition", "Ort", "Methode", "Level", "Chance", "Bedingung", header: true));
        foreach (var r in rows)
        {
            var version = versions.FirstOrDefault(v => v.Id == r.Version).Name ?? "";
            panel.Children.Add(Row(version, r.Location, r.Method,
                r.Min == r.Max ? r.Min.ToString() : $"{r.Min}–{r.Max}",
                $"{Math.Min(100, r.Rarity)} %", r.Conditions));
        }
        panel.Children.Add(Text("Chance = Anteil der Begegnungen an diesem Ort mit dieser Methode.", 11, opacity: 0.5));
        return panel;
    }

    private Control PokemonChip(int id)
    {
        var sprite = new Image { Width = 48, Height = 48 };
        RenderOptions.SetBitmapInterpolationMode(sprite, BitmapInterpolationMode.None);
        _ = SetSpriteAsync(sprite, id);
        var chip = new Border
        {
            Background = TileBrush,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 2),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children = { sprite, Text(_guide!.Pokemon.TryGetValue(id, out var p) ? p.Name : $"#{id}", bold: true) },
            },
        };
        chip.PointerPressed += (_, _) => Navigate("pokemon", id);
        return chip;
    }

    /// <summary>Attacken-Tabellen: Level, TM/VM, Zucht, Lehrer.</summary>
    private Control BuildLearnset(int id)
    {
        var g = _guide!;
        var panel = new StackPanel { Spacing = 8 };
        var entries = g.Learnsets.GetValueOrDefault(id)?.Where(l => l.VersionGroup == GuideVersionGroup).ToList() ?? [];
        if (entries.Count == 0)
        {
            panel.Children.Add(Text("In dieser Edition keine Daten – das Pokémon kommt dort evtl. nicht vor.", opacity: 0.7));
            return panel;
        }

        foreach (var method in new[] { 1, 4, 2, 3 }.Concat(entries.Select(e => (int)e.Method).Distinct().Where(m => m > 4)))
        {
            var list = entries.Where(e => e.Method == method)
                .OrderBy(e => e.Level).ThenBy(e => g.Moves.GetValueOrDefault(e.Move)?.Name)
                .DistinctBy(e => (e.Move, e.Level))
                .ToList();
            if (list.Count == 0) continue;

            var table = new StackPanel { Spacing = 2 };
            table.Children.Add(MoveRow(method == 1 ? "Lv." : "", "Attacke", null, "Kategorie", "Stärke", "Genauigk.", "AP", header: true));
            foreach (var e in list)
            {
                if (!g.Moves.TryGetValue(e.Move, out var m)) continue;
                table.Children.Add(MoveRow(method == 1 ? (e.Level == 0 ? "Entw." : e.Level.ToString()) : "",
                    m.Name, m, m.DamageClassName, m.Power?.ToString() ?? "–", m.Accuracy?.ToString() ?? "–", m.Pp?.ToString() ?? "–"));
            }
            panel.Children.Add(new Expander
            {
                Header = $"{GuideData.LearnMethodName(method)} ({list.Count})",
                Content = table,
                IsExpanded = method == 1,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            });
        }
        return panel;
    }

    private Control MoveRow(string level, string name, MoveInfo? move, string category, string power, string accuracy, string pp, bool header = false)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("55,*,110,95,65,80,45") };
        TextBlock Cell(string t) => Text(t, 13, bold: header, opacity: header ? 0.7 : 1);
        grid.Children.Add(Cell(level));
        grid.Children.Add(WithColumn(move is null ? Cell(name) : LinkText(name, () => Navigate("move", move.Id), 13), 1));
        grid.Children.Add(WithColumn(move is null ? Cell("Typ") : TypeBadge(move.Type, 12), 2));
        grid.Children.Add(WithColumn(Cell(category), 3));
        grid.Children.Add(WithColumn(Cell(power), 4));
        grid.Children.Add(WithColumn(Cell(accuracy), 5));
        grid.Children.Add(WithColumn(Cell(pp), 6));
        return grid;
    }

    // =====================================================================
    // Seite: Typ und Typ-Tabelle
    // =====================================================================

    private void ShowGuideType(int typeId)
    {
        var g = _guide!;
        int gen = GuideGeneration;
        if (!g.Types.TryGetValue(typeId, out var type)) return;
        var all = g.TypesIn(gen).Select(t => t.Id).ToList();

        GuideContent.Children.Add(Wrap([TypeBadge(typeId, 22), Text($"  Typ {type.Name}", 24, bold: true)]));

        StackPanel Section(string title, (string Label, IEnumerable<int> Types)[] rows)
        {
            var panel = new StackPanel { Spacing = 4 };
            foreach (var (label, ids) in rows)
            {
                var list = ids.ToList();
                panel.Children.Add(new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("230,*"),
                    Children = { Text(label), WithColumn(list.Count == 0 ? (Control)Text("–") : Wrap(list.Select(t => TypeBadge(t))), 1) },
                });
            }
            return panel;
        }

        GuideContent.Children.Add(Card($"Angriff mit {type.Name}-Attacken", Section("", [
            ("sehr effektiv (2×) gegen", all.Where(d => g.Effectiveness(typeId, d, gen) == 200)),
            ("nicht sehr effektiv (½×) gegen", all.Where(d => g.Effectiveness(typeId, d, gen) == 50)),
            ("wirkungslos (0×) gegen", all.Where(d => g.Effectiveness(typeId, d, gen) == 0)),
        ])));
        GuideContent.Children.Add(Card($"Verteidigung von {type.Name}-Pokémon", Section("", [
            ("anfällig (2×) für", all.Where(a => g.Effectiveness(a, typeId, gen) == 200)),
            ("resistent (½×) gegen", all.Where(a => g.Effectiveness(a, typeId, gen) == 50)),
            ("immun (0×) gegen", all.Where(a => g.Effectiveness(a, typeId, gen) == 0)),
        ])));

        var mons = g.Pokemon.Values.Where(p => p.Generation <= gen && p.TypesIn(gen).Contains(typeId)).OrderBy(p => p.Id).ToList();
        GuideContent.Children.Add(Card($"{type.Name}-Pokémon bis Gen {gen} ({mons.Count})",
            Wrap(mons.Select(p => (Control)new Border
            {
                Margin = new Thickness(0, 0, 10, 4),
                Child = LinkText($"#{p.Id:000} {p.Name}", () => Navigate("pokemon", p.Id), 13),
            }))));
    }

    private void ShowTypeChart()
    {
        var g = _guide!;
        int gen = GuideGeneration;
        var types = g.TypesIn(gen).ToList();

        GuideContent.Children.Add(Text($"Typ-Tabelle (Gen {gen})", 24, bold: true));
        GuideContent.Children.Add(Text("Zeile = angreifender Typ, Spalte = verteidigender Typ. " +
                                       "Grün = sehr effektiv (2×), Rot = nicht sehr effektiv (½×), Schwarz = keine Wirkung. " +
                                       "Typ anklicken für Details.", 13, opacity: 0.8));

        const double cell = 34;
        var grid = new Grid();
        for (int i = 0; i <= types.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(i == 0 ? new GridLength(110) : new GridLength(cell)));
            grid.RowDefinitions.Add(new RowDefinition(i == 0 ? new GridLength(110) : new GridLength(cell)));
        }

        for (int i = 0; i < types.Count; i++)
        {
            // Spaltenköpfe (gedreht) und Zeilenköpfe
            var columnHead = new LayoutTransformControl
            {
                LayoutTransform = new RotateTransform(-90),
                Child = TypeBadge(types[i].Id, 11),
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            Grid.SetColumn(columnHead, i + 1);
            grid.Children.Add(columnHead);

            var rowHead = TypeBadge(types[i].Id, 11);
            rowHead.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(rowHead, i + 1);
            grid.Children.Add(rowHead);
        }

        for (int a = 0; a < types.Count; a++)
            for (int d = 0; d < types.Count; d++)
            {
                int factor = g.Effectiveness(types[a].Id, types[d].Id, gen);
                var (bg, label) = factor switch
                {
                    200 => ("#2EB872", "2"),
                    50 => ("#C0392B", "½"),
                    0 => ("#111111", "0"),
                    _ => ("#00000000", ""),
                };
                var c = new Border
                {
                    Background = new SolidColorBrush(Color.Parse(bg)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
                    BorderThickness = new Thickness(0.5),
                    Child = new TextBlock
                    {
                        Text = label,
                        Foreground = Brushes.White,
                        FontWeight = FontWeight.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                ToolTip.SetTip(c, $"{types[a].Name} → {types[d].Name}: {factor / 100.0:0.##}×");
                Grid.SetRow(c, a + 1);
                Grid.SetColumn(c, d + 1);
                grid.Children.Add(c);
            }

        GuideContent.Children.Add(new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = grid });
    }

    // =====================================================================
    // Seite: Attacke
    // =====================================================================

    private void ShowGuideMove(int moveId)
    {
        var g = _guide!;
        if (!g.Moves.TryGetValue(moveId, out var m)) return;

        GuideContent.Children.Add(Text(m.Name, 26, bold: true));
        GuideContent.Children.Add(Wrap([TypeBadge(m.Type, 15), Text($"   {m.DamageClassName} · eingeführt in Gen {m.Generation}", opacity: 0.8)]));
        GuideContent.Children.Add(new WrapPanel
        {
            Children =
            {
                StatCard(m.Power?.ToString() ?? "–", "Stärke"),
                StatCard(m.Accuracy is { } acc ? $"{acc} %" : "–", "Genauigkeit"),
                StatCard(m.Pp?.ToString() ?? "–", "AP"),
                StatCard(m.Priority == 0 ? "0" : m.Priority.ToString("+0;-0"), "Priorität"),
            },
        });
        if (m.Description.Length > 0) GuideContent.Children.Add(Text(m.Description));

        // Wer lernt die Attacke in der gewählten Edition?
        int vg = GuideVersionGroup;
        var learners = g.Learnsets
            .SelectMany(kv => kv.Value.Where(l => l.Move == moveId && l.VersionGroup == vg).Select(l => (Pokemon: kv.Key, l.Method, l.Level)))
            .GroupBy(x => x.Method)
            .OrderBy(x => x.Key switch { 1 => 0, 4 => 1, 2 => 2, _ => 3 });

        bool any = false;
        foreach (var group in learners)
        {
            any = true;
            var items = group.DistinctBy(x => x.Pokemon).OrderBy(x => x.Pokemon)
                .Select(x => (Control)new Border
                {
                    Margin = new Thickness(0, 0, 12, 4),
                    Child = LinkText(group.Key == 1 && x.Level > 0
                        ? $"{g.Pokemon[x.Pokemon].Name} (Lv. {x.Level})"
                        : g.Pokemon[x.Pokemon].Name, () => Navigate("pokemon", x.Pokemon), 13),
                });
            GuideContent.Children.Add(Card($"{GuideData.LearnMethodName(group.Key)} ({group.DistinctBy(x => x.Pokemon).Count()})", Wrap(items)));
        }
        if (!any) GuideContent.Children.Add(Text("In der gewählten Edition lernt kein Pokémon diese Attacke.", opacity: 0.7));
    }
}
