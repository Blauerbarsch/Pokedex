using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DexHub.Core.Stats;

namespace DexHub.App;

/// <summary>Shiny-Jagden im Statistik-Reiter: laufende Zähler, neue Jagd, gefundene Shinys.</summary>
public partial class MainWindow
{
    private StackPanel _huntPanel = new();

    private void RebuildHunts()
    {
        var panel = _huntPanel;
        panel.Children.Clear();

        panel.Children.Add(Text(
            "Im Spiel zählt F8 eine Begegnung, F10 nimmt eine zurück (im Reiter Steuerung auch auf den Controller legbar). " +
            "Der Zähler steht dann oben rechts im Spielfenster.", 12, opacity: 0.7));

        // Neue Jagd
        var games = _games.Where(g => g.Info.Generation is >= 2).OrderBy(g => g.Info.Generation).ThenBy(g => g.Title).ToList();
        if (games.Count > 0)
        {
            var gameBox = new ComboBox { ItemsSource = games.Select(g => new GameChoice(g)).ToList(), SelectedIndex = 0, MinWidth = 260 };
            var speciesBox = SpeciesPicker("Gesuchtes Pokémon (optional)");
            var start = new Button { Content = "Jagd starten" };
            start.Click += (_, _) =>
            {
                if (gameBox.SelectedItem is not GameChoice choice) return;
                HuntStore.Add(new Hunt
                {
                    Profile = _settings.CurrentProfile,
                    Rom = Path.GetFileName(choice.Game.RomPath),
                    GameTitle = choice.Game.Title,
                    Generation = choice.Game.Info.Generation ?? 4,
                    Species = SpeciesFromText(speciesBox.Text),
                });
                RebuildHunts();
            };
            panel.Children.Add(new WrapPanel
            {
                Children = { Text("Neue Jagd:  ", opacity: 0.8), gameBox, Spacer(), speciesBox, Spacer(), start },
            });
        }

        var hunts = HuntStore.For(_settings.CurrentProfile);
        var active = hunts.Where(h => h.IsActive).OrderByDescending(h => h.Started).ToList();
        var found = hunts.Where(h => !h.IsActive).OrderByDescending(h => h.Found).ToList();

        if (active.Count == 0 && found.Count == 0)
            panel.Children.Add(Text("Noch keine Jagden.", opacity: 0.6));

        var activeWrap = new WrapPanel();
        foreach (var hunt in active) activeWrap.Children.Add(HuntCard(hunt));
        if (active.Count > 0) panel.Children.Add(activeWrap);

        if (found.Count > 0)
        {
            panel.Children.Add(Text($"Gefunden ({found.Count})", 15, bold: true));
            foreach (var h in found)
                panel.Children.Add(Text(
                    $"✨ {(h.Species > 0 ? SpeciesName(h.Species) : "Shiny")} nach {h.Count:N0} Begegnungen – {h.GameTitle}, " +
                    $"{h.Found:dd.MM.yyyy}  (Glück: {LuckText(h)})", 13));
        }
    }

    private static Control Spacer() => new Border { Width = 8 };

    private Border HuntCard(Hunt hunt)
    {
        var sprite = new Image { Width = 64, Height = 64 };
        RenderOptions.SetBitmapInterpolationMode(sprite, BitmapInterpolationMode.None);
        if (hunt.Species > 0) _ = SetShinySpriteAsync(sprite, hunt.Species);

        Button Small(string label, Action action)
        {
            var b = new Button { Content = label, Padding = new Thickness(10, 4) };
            b.Click += (_, _) => { action(); RebuildHunts(); };
            return b;
        }

        var speciesBox = SpeciesPicker(hunt.Species > 0 ? SpeciesName(hunt.Species) : "Pokémon festlegen …");
        if (hunt.Species > 0) speciesBox.Text = SpeciesName(hunt.Species);
        speciesBox.LostFocus += (_, _) =>
        {
            int species = SpeciesFromText(speciesBox.Text);
            if (species != hunt.Species) { HuntStore.Edit(hunt.Id, h => h.Species = species); RebuildHunts(); }
        };

        return new Border
        {
            Background = TileBrush,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 10, 10),
            Width = 330,
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Children =
                        {
                            sprite,
                            new StackPanel
                            {
                                VerticalAlignment = VerticalAlignment.Center,
                                Children =
                                {
                                    Text($"{hunt.Count:N0}", 30, bold: true),
                                    Text("Begegnungen", 12, opacity: 0.7),
                                },
                            },
                        },
                    },
                    Text(hunt.GameTitle, 13, bold: true),
                    speciesBox,
                    Text($"Chance, bis jetzt eins gesehen zu haben: {hunt.ChanceSoFar:P0}  (1/{(int)(1 / hunt.Odds)} je Begegnung)", 12, opacity: 0.7),
                    Text($"Seit {hunt.Started:dd.MM.yyyy}", 12, opacity: 0.5),
                    new WrapPanel
                    {
                        Children =
                        {
                            Small("+1", () => HuntStore.Edit(hunt.Id, h => h.Count++)),
                            Spacer(),
                            Small("−1", () => HuntStore.Edit(hunt.Id, h => h.Count = Math.Max(0, h.Count - 1))),
                            Spacer(),
                            Small("✨ Gefunden!", () => HuntStore.Edit(hunt.Id, h => h.Found = DateTime.Now)),
                            Spacer(),
                            Small("Löschen", () => HuntStore.Delete(hunt.Id)),
                        },
                    },
                },
            },
        };
    }

    private AutoCompleteBox SpeciesPicker(string watermark) => new()
    {
        Watermark = watermark,
        MinWidth = 220,
        FilterMode = AutoCompleteFilterMode.StartsWith,
        ItemsSource = _dex.Select(d => d.Name).ToList(),
    };

    private int SpeciesFromText(string? text)
    {
        text = text?.Trim() ?? "";
        if (text.Length == 0) return 0;
        if (int.TryParse(text.TrimStart('#'), out var number) && _dexById.ContainsKey(number)) return number;
        return _dex.FirstOrDefault(d => string.Equals(d.Name, text, StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(d.Species.NameEn, text, StringComparison.OrdinalIgnoreCase))?.Id ?? 0;
    }

    /// <summary>Im Vergleich zum Durchschnitt (1/Chance Begegnungen).</summary>
    private static string LuckText(Hunt h)
    {
        double expected = 1 / h.Odds;
        if (h.Count == 0) return "–";
        return h.Count <= expected ? $"schneller als der Schnitt ({h.ChanceSoFar:P0} hatten es bis dahin)" : $"zäh ({h.ChanceSoFar:P0} hätten es bis dahin)";
    }

    private async Task SetShinySpriteAsync(Image image, int species)
    {
        var path = await _pokeApi.GetSpritePathAsync(species, shiny: true) ?? await _pokeApi.GetSpritePathAsync(species);
        if (path is null) return;
        try { image.Source = new Bitmap(path); } catch { /* defektes Bild ignorieren */ }
    }
}
