using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DexHub.Core.Covers;
using DexHub.Core.Detection;
using DexHub.Core.Emulation;
using DexHub.Core.Models;
using DexHub.Core.Pokedex;
using DexHub.Core.Saves;
using DexHub.Core.Settings;

namespace DexHub.Mobile;

/// <summary>
/// Oberfläche der Android-App: Bibliothek, Pokédex, Info – und darüber das Spiel, wenn eins läuft.
/// Alles im Code aufgebaut (eine Ansicht, für Touch optimiert).
/// </summary>
public sealed class MobileView : UserControl
{
    public static string RomFolder => Path.Combine(AppSettings.AppDataFolder, "roms");

    private readonly PokeApiRepository _pokeApi = new();
    private readonly CoverService _covers = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private List<GameEntry> _games = [];
    private List<PokemonSpecies> _species = [];

    private readonly Grid _root = new();
    private readonly TabControl _tabs = new() { Padding = new Thickness(0) };
    private readonly StackPanel _library = new() { Spacing = 8, Margin = new Thickness(12) };
    private readonly WrapPanel _dexTiles = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _dexCounter = Text("", 14, opacity: 0.8);
    private readonly ComboBox _dexGeneration = new() { MinWidth = 160 };
    private readonly TextBlock _status = Text("", 13, opacity: 0.7);
    private PlayView? _play;

    private static readonly IBrush TileBrush = new SolidColorBrush(Color.Parse("#22FFFFFF"));

    public MobileView()
    {
        Directory.CreateDirectory(RomFolder);
        AppSettings.ActiveProfile = AppSettings.DefaultProfile;

        _tabs.Items.Add(new TabItem { Header = "Bibliothek", Content = new ScrollViewer { Content = _library } });
        _tabs.Items.Add(new TabItem { Header = "Pokédex", Content = BuildDexTab() });
        _tabs.Items.Add(new TabItem { Header = "Info", Content = new ScrollViewer { Content = BuildInfoTab() } });
        _root.Children.Add(_tabs);
        Content = _root;

        AttachedToVisualTree += async (_, _) =>
        {
            ScanLibrary();
            await LoadPokedexAsync();
        };
    }

    // =====================================================================
    // Bibliothek
    // =====================================================================

    private void ScanLibrary()
    {
        _games = GameDetector.ScanFolder(RomFolder, _settings.ShowAllGames);
        foreach (var g in _games) g.SavePath = SaveLocator.Find(g);
        BuildLibrary();
        RebuildDex();
    }

    private void BuildLibrary()
    {
        _library.Children.Clear();
        var add = new Button { Content = "＋ ROMs hinzufügen", Padding = new Thickness(16, 10), FontSize = 16 };
        add.Click += async (_, _) => await ImportRomsAsync();
        _library.Children.Add(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                Text("DexHub", 26, bold: true),
                Text("Deine Pokémon-Spiele. ROMs bringst du selbst mit – sie werden in die App kopiert.", 13, opacity: 0.7),
                add,
                _status,
            },
        });

        if (_games.Count == 0)
        {
            _library.Children.Add(Text("Noch keine Spiele. Tippe auf „ROMs hinzufügen“ und wähle .gb, .gbc, .gba oder .nds-Dateien.", 14, opacity: 0.7));
            return;
        }

        foreach (var platform in _games.GroupBy(g => g.Info.Platform).OrderBy(g => g.Key))
        {
            _library.Children.Add(Text(platform.Key.DisplayName(), 18, bold: true));
            foreach (var game in platform) _library.Children.Add(GameRow(game));
        }
    }

    private Control GameRow(GameEntry game)
    {
        var cover = new Image { Width = 64, Height = 64, Stretch = Stretch.Uniform };
        _ = LoadCoverAsync(game, cover);

        bool playable = CoreCatalog.FindCore(game.Info.Platform) is not null;
        var row = new Border
        {
            Background = TileBrush,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Children =
                {
                    cover,
                    new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            Text(game.Title, 16, bold: true),
                            Text(playable ? (game.SavePath is null ? "Noch kein Spielstand" : "Spielstand vorhanden")
                                          : "Auf Android noch nicht spielbar", 12, opacity: 0.6),
                        },
                    },
                },
            },
        };
        if (playable) row.Tapped += (_, _) => StartGame(game);

        var menu = new ContextMenu();
        var fresh = new MenuItem { Header = "Neu starten (ohne Fortsetzen)" };
        fresh.Click += (_, _) => StartGame(game, resume: false);
        var remove = new MenuItem { Header = "Aus der App entfernen (Spielstand bleibt)" };
        remove.Click += (_, _) =>
        {
            try { File.Delete(game.RomPath); } catch (IOException) { }
            ScanLibrary();
        };
        menu.Items.Add(fresh);
        menu.Items.Add(remove);
        row.ContextMenu = menu; // langes Drücken
        return row;
    }

    private async Task LoadCoverAsync(GameEntry game, Image target)
    {
        try
        {
            var path = await _covers.GetCoverPathAsync(game);
            if (path is not null) target.Source = new Bitmap(path);
        }
        catch (Exception) { /* ohne Cover weiter */ }
    }

    private async Task ImportRomsAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "ROMs auswählen",
            AllowMultiple = true,
        });
        if (files.Count == 0) return;

        int copied = 0;
        foreach (var file in files)
        {
            var name = file.Name;
            if (!GameDetectorExtensions.IsRom(name)) continue;
            _status.Text = $"Kopiere {name} …";
            try
            {
                await using var source = await file.OpenReadAsync();
                await using var target = File.Create(Path.Combine(RomFolder, name));
                await source.CopyToAsync(target);
                copied++;
            }
            catch (Exception ex)
            {
                _status.Text = $"{name}: {ex.Message}";
            }
        }
        _status.Text = copied == 0 ? "Keine passenden Dateien (.gb, .gbc, .gba, .nds, .min)." : $"{copied} Datei(en) hinzugefügt.";
        ScanLibrary();
    }

    // =====================================================================
    // Spiel
    // =====================================================================

    private void StartGame(GameEntry game, bool resume = true)
    {
        if (_play is not null) return;
        var core = CoreCatalog.FindCore(game.Info.Platform);
        if (core is null)
        {
            _status.Text = "Für diese Konsole fehlt der Emulator in dieser App-Version.";
            return;
        }

        _play = new PlayView(game, core, resume, _settings);
        _play.Closed += () =>
        {
            _root.Children.Remove(_play);
            _play = null;
            _tabs.IsVisible = true;
            MainActivity.Current?.SetImmersive(false);
            foreach (var g in _games) g.SavePath = SaveLocator.Find(g);
            BuildLibrary();
            RebuildDex();
        };
        _tabs.IsVisible = false;
        _root.Children.Add(_play);
        MainActivity.Current?.SetImmersive(true);
    }

    public void SetBackground(bool background) => _play?.SetBackground(background);

    public bool HandleBack()
    {
        if (_play is null) return false;
        _play.RequestClose();
        return true;
    }

    // =====================================================================
    // Pokédex
    // =====================================================================

    private Control BuildDexTab()
    {
        _dexGeneration.ItemsSource = Enumerable.Range(1, 9).Select(g => $"Generation {g}").ToList();
        _dexGeneration.SelectedIndex = 0;
        _dexGeneration.SelectionChanged += (_, _) => RebuildDex();
        return new DockPanel
        {
            Margin = new Thickness(8),
            Children =
            {
                new StackPanel
                {
                    [DockPanel.DockProperty] = Dock.Top,
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Margin = new Thickness(4, 0, 4, 8),
                    Children = { _dexGeneration, _dexCounter },
                },
                new ScrollViewer { Content = _dexTiles },
            },
        };
    }

    private async Task LoadPokedexAsync()
    {
        try
        {
            _dexCounter.Text = "Lade Pokédex …";
            _species = await _pokeApi.LoadSpeciesAsync();
            RebuildDex();
        }
        catch (Exception ex)
        {
            _dexCounter.Text = "Pokédex nicht geladen (Internet?): " + ex.Message;
        }
    }

    private void RebuildDex()
    {
        if (_species.Count == 0) return;
        var dex = PokedexService.Build(_species, _games, AppSettings.DefaultProfile);
        int gen = _dexGeneration.SelectedIndex + 1;
        var shown = dex.Where(d => d.Species.Generation == gen).ToList();
        _dexCounter.Text = $"{shown.Count(d => d.IsCaught)} / {shown.Count} gefangen · gesamt {dex.Count(d => d.IsCaught)}";

        _dexTiles.Children.Clear();
        foreach (var entry in shown)
        {
            var sprite = new Image { Width = 64, Height = 64 };
            RenderOptions.SetBitmapInterpolationMode(sprite, BitmapInterpolationMode.None);
            if (entry.SeenIn.Count > 0 || entry.IsCaught) _ = LoadSpriteAsync(entry.Id, sprite);
            _dexTiles.Children.Add(new Border
            {
                Width = 96,
                Margin = new Thickness(3),
                Padding = new Thickness(4),
                CornerRadius = new CornerRadius(6),
                Background = entry.IsCaught ? new SolidColorBrush(Color.Parse("#334CAF50")) : TileBrush,
                Child = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        sprite,
                        Text($"{entry.Number} {entry.CaughtMarker}", 11, opacity: 0.6),
                        Text(entry.IsCaught || entry.SeenIn.Count > 0 ? entry.Name : "???", 12),
                    },
                },
            });
        }
    }

    private async Task LoadSpriteAsync(int id, Image target)
    {
        try
        {
            var path = await _pokeApi.GetSpritePathAsync(id);
            if (path is not null) target.Source = new Bitmap(path);
        }
        catch (Exception) { }
    }

    // =====================================================================
    // Info
    // =====================================================================

    private Control BuildInfoTab()
    {
        var showAll = new CheckBox { Content = "Auch Spiele ohne Pokémon anzeigen", IsChecked = _settings.ShowAllGames };
        showAll.IsCheckedChanged += (_, _) =>
        {
            _settings.ShowAllGames = showAll.IsChecked == true;
            _settings.Save();
            ScanLibrary();
        };
        return new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(12),
            Children =
            {
                Text("DexHub für Android", 22, bold: true),
                Text("Steuerung: Tasten auf dem Bildschirm oder ein Controller (Bluetooth oder eingebaut). " +
                     "Am Controller: R2 = Vorspulen. Beim DS tippst du direkt auf den unteren Bildschirm. " +
                     "Die Zurück-Taste beendet das Spiel (es wird automatisch gespeichert und beim nächsten Start fortgesetzt).", 14),
                showAll,
                Text("Spielstände liegen in:", 13, bold: true),
                Text(AppSettings.SavesFolder, 12, opacity: 0.7),
                Text("Noch nicht in der Android-Version: Statistik, Guide, Tauschen, Bank. Kommt nach und nach.", 13, opacity: 0.7),
            },
        };
    }

    // =====================================================================

    internal static TextBlock Text(string text, double size = 14, bool bold = false, double opacity = 1) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
        Opacity = opacity,
        TextWrapping = TextWrapping.Wrap,
    };
}

internal static class GameDetectorExtensions
{
    public static bool IsRom(string name) =>
        Path.GetExtension(name).ToLowerInvariant() is ".gb" or ".gbc" or ".gba" or ".nds" or ".min";
}
