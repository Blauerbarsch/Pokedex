using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.Controls.Primitives;
using DexHub.App.Audio;
using DexHub.App.Input;
using DexHub.Core.Libretro;
using DexHub.Core.Covers;
using DexHub.Core.Detection;
using DexHub.Core.Emulation;
using DexHub.Core.Models;
using DexHub.Core.Pokedex;
using DexHub.Core.Saves;
using DexHub.Core.Settings;
using GamePlatform = DexHub.Core.Models.Platform;

namespace DexHub.App;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly PokeApiRepository _pokeApi = new();

    private List<GameEntry> _games = [];
    private List<PokemonSpecies> _species = [];
    private List<DexEntry> _dex = [];

    private GameWindow? _runningGame;
    private CancellationTokenSource? _spriteCts;
    private bool _ready; // verhindert, dass Events beim Laden der Oberfläche zu früh feuern

    public MainWindow()
    {
        InitializeComponent();
        FillSettingsForm();
        _ready = true;

        // Tastendrücke abfangen, bevor ein Steuerelement sie bekommt (für "Taste belegen")
        AddHandler(KeyDownEvent, OnCaptureKeyDown, RoutingStrategies.Tunnel);

        WatchSaveFolder();

        Opened += async (_, _) =>
        {
            await ScanLibraryAsync();
            await LoadPokedexAsync(forceRefresh: false);
        };

        // Läuft beim Schließen noch ein Spiel, dieses zuerst sauber beenden (Spielstand speichern)
        Closing += (_, _) => _runningGame?.Close();
    }

    // =====================================================================
    // Bibliothek
    // =====================================================================

    private async Task ScanLibraryAsync()
    {
        if (string.IsNullOrWhiteSpace(_settings.RomFolder))
        {
            SetStatus("Bitte zuerst unter „Einstellungen“ einen ROM-Ordner wählen.");
            return;
        }

        var romFolder = _settings.RomFolder;
        if (!Directory.Exists(romFolder))
        {
            SetStatus($"ROM-Ordner nicht erreichbar: {romFolder} – ist das Netzlaufwerk verbunden?");
            return;
        }

        SetStatus("Durchsuche ROM-Ordner… (bei Netzlaufwerken kann das etwas dauern)");
        try
        {
            _games = await Task.Run(() =>
            {
                var games = GameDetector.ScanFolder(romFolder);
                foreach (var g in games)
                    g.SavePath = SaveLocator.Find(g);
                return games;
            });
        }
        catch (Exception ex)
        {
            SetStatus("Fehler beim Durchsuchen: " + ex.Message);
            return;
        }

        RefreshGamesList();
        RebuildDex();
        SetStatus($"{_games.Count} Spiel(e) gefunden.");
    }

    // ---------- Kachel-Ansicht ----------

    private readonly CoverService _covers = new();
    private readonly Dictionary<GameEntry, Border> _tiles = [];
    private GameEntry? _selectedGame;

    private static readonly IBrush TileBrush = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
    private static readonly IBrush SelectedBrush = new SolidColorBrush(Color.Parse("#E3350D"));

    /// <summary>Baut die Bibliothek neu auf: pro Konsole ein einklappbarer Bereich mit Spiel-Kacheln.</summary>
    private void RefreshGamesList()
    {
        LibraryPanel.Children.Clear();
        _tiles.Clear();

        var query = LibrarySearchBox.Text?.Trim() ?? "";
        var visible = _games.Where(g => query.Length == 0
            || g.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || g.FileName.Contains(query, StringComparison.OrdinalIgnoreCase));

        foreach (var group in visible.GroupBy(g => g.Info.Platform).OrderBy(g => g.Key))
        {
            var wrap = new WrapPanel();
            foreach (var game in group.OrderBy(g => g.Info.Generation ?? 99).ThenBy(g => g.Title))
                wrap.Children.Add(CreateTile(game));

            var platform = group.Key;
            var expander = new Expander
            {
                Header = $"{platform.DisplayName()}  ({group.Count()})",
                Content = wrap,
                IsExpanded = !_settings.CollapsedPlatforms.Contains(platform),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            // Auf-/Zuklappen merken
            expander.PropertyChanged += (_, e) =>
            {
                if (e.Property != Expander.IsExpandedProperty) return;
                if (expander.IsExpanded) _settings.CollapsedPlatforms.Remove(platform);
                else _settings.CollapsedPlatforms.Add(platform);
                _settings.Save();
            };
            LibraryPanel.Children.Add(expander);
        }

        if (_selectedGame is not null && !_tiles.ContainsKey(_selectedGame)) _selectedGame = null;
        SelectGame(_selectedGame);
    }

    private Border CreateTile(GameEntry game)
    {
        var cover = new Image { Width = 140, Height = 140, Stretch = Stretch.Uniform };
        var placeholder = new TextBlock
        {
            Text = game.PlatformName,
            Opacity = 0.4,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var coverHost = new Grid { Width = 140, Height = 140, Children = { placeholder, cover } };

        var tile = new Border
        {
            Width = 170,
            Margin = new Thickness(6),
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(8),
            Background = TileBrush,
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    coverHost,
                    new TextBlock
                    {
                        Text = game.Title,
                        FontWeight = FontWeight.SemiBold,
                        TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text = game.StatusText,
                        FontSize = 11,
                        Opacity = 0.6,
                        TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Center,
                    },
                },
            },
        };

        tile.PointerPressed += async (_, e) =>
        {
            SelectGame(game);
            if (e.ClickCount == 2 && e.GetCurrentPoint(tile).Properties.IsLeftButtonPressed)
                await LaunchGameAsync(game);
        };

        var play = new MenuItem { Header = "▶ Spielen" };
        play.Click += async (_, _) => await LaunchGameAsync(game);
        var fresh = new MenuItem { Header = "Ohne Fortsetzen starten" };
        fresh.Click += async (_, _) => await LaunchGameAsync(game, resume: false);
        var info = new MenuItem { Header = "Datei: " + game.FileName, IsEnabled = false };
        tile.ContextMenu = new ContextMenu { ItemsSource = new object[] { play, fresh, new Separator(), info } };

        _tiles[game] = tile;
        _ = LoadCoverAsync(game, cover, placeholder);
        return tile;
    }

    private async Task LoadCoverAsync(GameEntry game, Image target, TextBlock placeholder)
    {
        try
        {
            var path = await _covers.GetCoverPathAsync(game);
            if (path is null) return;
            target.Source = new Bitmap(path);
            placeholder.IsVisible = false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DexHub] Cover für {game.Title} fehlgeschlagen: {ex.Message}");
        }
    }

    private void SelectGame(GameEntry? game)
    {
        _selectedGame = game;
        foreach (var (g, tile) in _tiles)
            tile.BorderBrush = g == game ? SelectedBrush : Brushes.Transparent;
        SelectedGameText.Text = game is null
            ? "Doppelklick startet ein Spiel, Rechtsklick zeigt mehr."
            : $"Ausgewählt: {game.Title}";
    }

    private void OnLibrarySearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (_ready) RefreshGamesList();
    }

    private async void OnRescanClick(object? sender, RoutedEventArgs e) => await ScanLibraryAsync();

    private async void OnPlayClick(object? sender, RoutedEventArgs e) => await LaunchSelectedAsync();

    private async void OnPlayFreshClick(object? sender, RoutedEventArgs e) => await LaunchSelectedAsync(resume: false);

    private async Task LaunchSelectedAsync(bool resume = true)
    {
        if (_selectedGame is null)
        {
            SetStatus("Bitte zuerst ein Spiel auswählen.");
            return;
        }
        await LaunchGameAsync(_selectedGame, resume);
    }

    private async Task LaunchGameAsync(GameEntry game, bool resume = true)
    {
        // Es kann immer nur ein Spiel gleichzeitig laufen (Einschränkung von libretro)
        if (_runningGame is not null)
        {
            _runningGame.Activate();
            SetStatus("Es läuft bereits ein Spiel. Bitte zuerst dessen Fenster schließen.");
            return;
        }

        var platform = game.Info.Platform;
        if (!CoreCatalog.IsPlatformSupported(platform))
        {
            await ShowMessageAsync("Noch nicht spielbar",
                $"{platform.DisplayName()}-Spiele werden in einem späteren Schritt eingebaut.");
            return;
        }

        var corePath = CoreCatalog.FindCore(platform);
        if (corePath is null)
        {
            await ShowMessageAsync("Emulator fehlt",
                $"Der Emulator für {platform.DisplayName()} ({CoreCatalog.Cores[platform]}) wurde nicht gefunden.\n\n" +
                "Beim Entwickeln einmal im Projektordner ausführen:\n  dotnet run scripts/FetchCores.cs");
            return;
        }

        var session = new EmulatorSession(game, corePath, new SdlAudioSink());
        SdlGamepad.Instance.Settings = _settings.Input;
        session.InputProvider = SdlGamepad.Instance;
        session.AutoResume = resume && _settings.AutoResume;
        session.CoreOptions = new(_settings.CoreOptions.GetValueOrDefault(session.CoreName) ?? []);

        // Speichert das Spiel, sofort den Pokédex aktualisieren
        session.SaveWritten += _ => Dispatcher.UIThread.Post(() =>
        {
            game.SavePath = SaveLocator.Find(game);
            RebuildDex();
            SetStatus($"Spielstand von {game.Title} gesichert – Pokédex aktualisiert.");
        });

        var window = new GameWindow(session, _settings);
        window.VideoSettingsChanged += BuildVideoPanel;
        window.Closed += (_, _) =>
        {
            _runningGame = null;
            BuildVideoPanel(); // jetzt sind evtl. neue Emulator-Optionen bekannt
            game.SavePath = SaveLocator.Find(game);
            RefreshGamesList();
            RebuildDex();
            SetStatus($"{game.Title} beendet.");
        };

        _runningGame = window;
        window.Show();
        SetStatus($"{game.Title} läuft…");
    }

    // =====================================================================
    // Pokédex
    // =====================================================================

    private FileSystemWatcher? _saveWatcher;
    private DispatcherTimer? _saveChangeDebounce;

    /// <summary>
    /// Manche Emulatoren (z. B. DS) schreiben ihre Spielstände selbst. Ändert sich eine Datei im
    /// Spielstand-Ordner, wird der Pokédex kurz darauf neu eingelesen.
    /// </summary>
    private void WatchSaveFolder()
    {
        try
        {
            _saveWatcher = new FileSystemWatcher(AppSettings.SavesFolder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            FileSystemEventHandler changed = (_, _) => Dispatcher.UIThread.Post(() =>
            {
                // Mehrere Änderungen kurz hintereinander zusammenfassen
                _saveChangeDebounce?.Stop();
                _saveChangeDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _saveChangeDebounce.Tick += (_, _) =>
                {
                    _saveChangeDebounce?.Stop();
                    foreach (var g in _games) g.SavePath = SaveLocator.Find(g);
                    RebuildDex();
                };
                _saveChangeDebounce.Start();
            });
            _saveWatcher.Changed += changed;
            _saveWatcher.Created += changed;
            _saveWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[DexHub] Spielstand-Ordner kann nicht überwacht werden: " + ex.Message);
        }
    }

    private async Task LoadPokedexAsync(bool forceRefresh)
    {
        SetStatus("Lade Pokédex-Daten…");
        try
        {
            _species = await _pokeApi.LoadSpeciesAsync(forceRefresh);
            RebuildDex();
            SetStatus($"Pokédex geladen: {_species.Count} Pokémon.");
        }
        catch (Exception ex)
        {
            SetStatus("Pokédex-Daten konnten nicht geladen werden. Besteht eine Internetverbindung?");
            System.Diagnostics.Trace.WriteLine(ex);
        }
    }

    // ---------- Pokédex als Kacheln, nach Generationen ----------

    private static readonly string[] Regions =
        ["", "Kanto", "Johto", "Hoenn", "Sinnoh", "Einall", "Kalos", "Alola", "Galar", "Paldea"];

    /// <summary>Eine Generation im Pokédex: einklappbarer Bereich, Kacheln werden erst beim Aufklappen erzeugt.</summary>
    private sealed class DexGroup
    {
        public required int Generation { get; init; }
        public required Expander Expander { get; init; }
        public required WrapPanel Panel { get; init; }
        public bool Realized { get; set; }
    }

    private sealed record DexTile(Border Tile, TextBlock Marker);

    private readonly List<DexGroup> _dexGroups = [];
    private readonly Dictionary<int, DexTile> _dexTiles = [];
    private Dictionary<int, DexEntry> _dexById = [];
    private int _selectedDexId;

    private void RebuildDex()
    {
        _dex = PokedexService.Build(_species, _games);
        _dexById = _dex.ToDictionary(d => d.Id);

        // Gruppen nur einmal anlegen (bzw. wenn sich die Anzahl Generationen ändert)
        var generations = _dex.Select(d => d.Species.Generation).Distinct().Order().ToList();
        if (!generations.SequenceEqual(_dexGroups.Select(g => g.Generation)))
            BuildDexGroups(generations);

        foreach (var (id, tile) in _dexTiles)
            if (_dexById.TryGetValue(id, out var entry)) StyleDexTile(tile, entry);

        ApplyDexFilter();

        // Detailansicht aktuell halten
        if (_dexById.TryGetValue(_selectedDexId, out var selected)) DetailText.Text = selected.DetailText;
    }

    private void BuildDexGroups(List<int> generations)
    {
        DexPanel.Children.Clear();
        _dexGroups.Clear();
        _dexTiles.Clear();

        foreach (var gen in generations)
        {
            var panel = new WrapPanel();
            var expander = new Expander
            {
                Content = panel,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var group = new DexGroup { Generation = gen, Expander = expander, Panel = panel };
            _dexGroups.Add(group);

            expander.PropertyChanged += (_, e) =>
            {
                if (e.Property != Expander.IsExpandedProperty) return;
                if (expander.IsExpanded)
                {
                    RealizeDexGroup(group);
                    ApplyDexFilter();
                    _settings.ExpandedGenerations.Add(gen);
                }
                else
                {
                    _settings.ExpandedGenerations.Remove(gen);
                }
                _settings.Save();
            };

            // Erst nach dem Anhängen des Handlers setzen, damit die Kacheln entstehen
            expander.IsExpanded = _settings.ExpandedGenerations.Contains(gen);
            DexPanel.Children.Add(expander);
        }
    }

    /// <summary>Kacheln einer Generation erzeugen (erst beim ersten Aufklappen – spart Zeit beim Start).</summary>
    private void RealizeDexGroup(DexGroup group)
    {
        if (group.Realized) return;
        group.Realized = true;

        foreach (var entry in _dex.Where(d => d.Species.Generation == group.Generation))
        {
            var sprite = new Image { Width = 96, Height = 96, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapInterpolationMode(sprite, BitmapInterpolationMode.None);

            var marker = new TextBlock { Foreground = SelectedBrush, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Right };
            var tile = new Border
            {
                Width = 124,
                Margin = new Thickness(4),
                Padding = new Thickness(6),
                CornerRadius = new CornerRadius(8),
                Background = TileBrush,
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new Grid
                        {
                            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                            Children = { new TextBlock { Text = entry.Number, Opacity = 0.6, FontSize = 12 }, WithColumn(marker, 1) },
                        },
                        sprite,
                        new TextBlock
                        {
                            Text = entry.Name,
                            FontWeight = FontWeight.SemiBold,
                            TextAlignment = TextAlignment.Center,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        },
                    },
                },
            };

            var id = entry.Id;
            tile.PointerPressed += async (_, _) => await ShowDexDetailAsync(id);

            var dexTile = new DexTile(tile, marker);
            _dexTiles[id] = dexTile;
            StyleDexTile(dexTile, entry);
            group.Panel.Children.Add(tile);
            _ = LoadSpriteAsync(id, sprite);
        }
    }

    private static Control WithColumn(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }

    /// <summary>Gefangen = voll sichtbar mit ●, gesehen = ○, unbekannt = blass.</summary>
    private void StyleDexTile(DexTile tile, DexEntry entry)
    {
        tile.Marker.Text = entry.CaughtMarker;
        tile.Tile.Opacity = entry.IsCaught ? 1.0 : entry.SeenIn.Count > 0 ? 0.75 : 0.45;
        tile.Tile.BorderBrush = entry.Id == _selectedDexId ? SelectedBrush : Brushes.Transparent;
    }

    private async Task LoadSpriteAsync(int id, Image target)
    {
        var path = await _pokeApi.GetSpritePathAsync(id);
        if (path is null) return;
        try { target.Source = new Bitmap(path); }
        catch (Exception ex) { Console.WriteLine($"[DexHub] Bild #{id} fehlerhaft: {ex.Message}"); }
    }

    private void ApplyDexFilter()
    {
        var query = SearchBox.Text?.Trim() ?? "";
        int number = int.TryParse(query.TrimStart('#'), out var n) ? n : -1;
        int genFilter = GenFilter.SelectedIndex; // 0 = alle
        bool onlyCaught = OnlyCaughtBox.IsChecked == true;

        bool Matches(DexEntry d) =>
            (query.Length == 0 || d.Id == number
                || d.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || d.Species.NameEn.Contains(query, StringComparison.OrdinalIgnoreCase))
            && (genFilter <= 0 || d.Species.Generation == genFilter)
            && (!onlyCaught || d.IsCaught);

        int totalShown = 0, totalCaught = 0;
        foreach (var group in _dexGroups)
        {
            var entries = _dex.Where(d => d.Species.Generation == group.Generation).ToList();
            var matching = entries.Where(Matches).ToList();
            int caught = entries.Count(d => d.IsCaught);

            group.Expander.IsVisible = matching.Count > 0;
            group.Expander.Header = $"Generation {group.Generation} – {Regions.ElementAtOrDefault(group.Generation)}" +
                                    $"   ·   {caught} / {entries.Count} gefangen";

            // Bei einer Suche Bereiche mit Treffern automatisch aufklappen
            if (query.Length > 0 && matching.Count > 0 && !group.Expander.IsExpanded)
                group.Expander.IsExpanded = true;

            if (group.Realized)
            {
                var visible = matching.Select(d => d.Id).ToHashSet();
                foreach (var d in entries)
                    if (_dexTiles.TryGetValue(d.Id, out var tile)) tile.Tile.IsVisible = visible.Contains(d.Id);
            }

            totalShown += matching.Count;
            totalCaught += matching.Count(d => d.IsCaught);
        }

        DexCounter.Text = $"{totalCaught} / {totalShown} gefangen";
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) { if (_ready) ApplyDexFilter(); }
    private void OnGenFilterChanged(object? sender, SelectionChangedEventArgs e) { if (_ready) ApplyDexFilter(); }
    private void OnOnlyCaughtChanged(object? sender, RoutedEventArgs e) { if (_ready) ApplyDexFilter(); }

    private async void OnReloadDexClick(object? sender, RoutedEventArgs e) => await LoadPokedexAsync(forceRefresh: true);

    private async Task ShowDexDetailAsync(int id)
    {
        if (!_dexById.TryGetValue(id, out var entry)) return;

        int previous = _selectedDexId;
        _selectedDexId = id;
        foreach (var other in new[] { previous, id })
            if (_dexTiles.TryGetValue(other, out var t) && _dexById.TryGetValue(other, out var e)) StyleDexTile(t, e);

        DetailName.Text = $"{entry.Number} {entry.Name}";
        DetailText.Text = entry.DetailText;
        SpriteImage.Source = null;

        // Bild nachladen; ältere Ladevorgänge abbrechen, wenn man schnell weiterklickt
        _spriteCts?.Cancel();
        _spriteCts = new CancellationTokenSource();
        var token = _spriteCts.Token;

        try
        {
            var path = await _pokeApi.GetSpritePathAsync(id, token);
            if (!token.IsCancellationRequested && path is not null)
                SpriteImage.Source = new Bitmap(path);
        }
        catch (OperationCanceledException)
        {
            // Absichtlich ignoriert
        }
    }

    // =====================================================================
    // Einstellungen
    // =====================================================================

    private void FillSettingsForm()
    {
        RomFolderBox.Text = _settings.RomFolder;

        var lines = Enum.GetValues<GamePlatform>()
            .Where(p => p != GamePlatform.Unknown)
            .Select(p => (p.DisplayName() + ":").PadRight(20) + (
                !CoreCatalog.IsPlatformSupported(p) ? "kommt später"
                : CoreCatalog.FindCore(p) is not null ? $"✓ {CoreCatalog.Cores[p]}"
                : $"✗ {CoreCatalog.Cores[p]} fehlt"));
        CoreStatusText.Text = string.Join("\n", lines);

        VolumeSlider.Value = _settings.Volume;
        VolumeText.Text = $"{_settings.Volume} %";
        FastForwardBox.SelectedIndex = Math.Max(0, Array.IndexOf(FastForwardSpeeds, _settings.FastForwardSpeed));
        BuildVideoPanel();
        AutoResumeBox.IsChecked = _settings.AutoResume;
        FastForwardModeBox.SelectedIndex = (int)_settings.FastForwardMode;
        BuildControlsPanel();

        FoldersText.Text =
            $"Spielstände: {AppSettings.SavesFolder}\n" +
            $"Schnellspeicherungen: {AppSettings.StatesFolder}\n" +
            $"BIOS/Firmware (optional): {AppSettings.SystemFolder}";
    }

    private static readonly int[] FastForwardSpeeds = [2, 3, 4, 8, 0];

    private void OnVolumeChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_ready) return;
        _settings.Volume = (int)Math.Round(VolumeSlider.Value);
        VolumeText.Text = $"{_settings.Volume} %";
        SaveAndApply();
    }

    private void OnFastForwardChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || FastForwardBox.SelectedIndex < 0) return;
        _settings.FastForwardSpeed = FastForwardSpeeds[FastForwardBox.SelectedIndex];
        SaveAndApply();
    }


    private void OnFastForwardModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || FastForwardModeBox.SelectedIndex < 0) return;
        _settings.FastForwardMode = (FastForwardMode)FastForwardModeBox.SelectedIndex;
        SaveAndApply();
    }

    private void OnAutoResumeChanged(object? sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _settings.AutoResume = AutoResumeBox.IsChecked == true;
        _settings.Save();
    }

    /// <summary>Speichert und gibt Änderungen sofort an ein laufendes Spiel weiter.</summary>
    private void SaveAndApply()
    {
        _settings.Save();
        _runningGame?.ApplySettings();
    }

    // =====================================================================
    // Bild-Einstellungen pro Konsole
    // =====================================================================

    private readonly HashSet<GamePlatform> _expandedVideoSections = [];

    private void BuildVideoPanel()
    {
        VideoPanel.Children.Clear();
        VideoPanel.Children.Add(new TextBlock
        {
            Text = "Einstellungen pro Konsole. Änderungen wirken sofort, auch im laufenden Spiel. " +
                   "Im Spiel: F4 = Bildeffekt wechseln, F2 = DS-Anordnung, F3 = DS-Bildschirme tauschen " +
                   "(änderbar im Tab Steuerung).",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
        });

        foreach (var platform in CoreCatalog.Cores.Keys)
        {
            var video = _settings.VideoFor(platform);
            var content = new StackPanel { Spacing = 10 };

            var smooth = new CheckBox { Content = "Weichzeichnen (aus = scharfe Pixel wie im Original)", IsChecked = video.Smooth };
            smooth.IsCheckedChanged += (_, _) => { video.Smooth = smooth.IsChecked == true; SaveAndApply(); };
            content.Children.Add(LabeledRow("Pixel", smooth));

            content.Children.Add(LabeledRow("Skalierung",
                Choice(VideoSettings.ScalingLabels, (int)video.Scaling, i => video.Scaling = (ScalingMode)i)));
            content.Children.Add(LabeledRow("Bildeffekt",
                Choice(VideoSettings.EffectLabels, (int)video.Effect, i => video.Effect = (OverlayEffect)i)));

            if (platform == GamePlatform.NintendoDS)
            {
                content.Children.Add(LabeledRow("Bildschirm-Anordnung",
                    Choice(VideoSettings.DsLayoutLabels, (int)video.DsLayout, i => video.DsLayout = (DsLayout)i)));
                var swap = new CheckBox { Content = "Oberen und unteren Bildschirm tauschen", IsChecked = video.DsSwapScreens };
                swap.IsCheckedChanged += (_, _) => { video.DsSwapScreens = swap.IsChecked == true; SaveAndApply(); };
                content.Children.Add(LabeledRow("Tauschen", swap));
            }

            content.Children.Add(BuildCoreOptionsExpander(CoreCatalog.Cores[platform]));

            var section = new Expander
            {
                Header = platform.DisplayName(),
                Content = content,
                IsExpanded = _expandedVideoSections.Contains(platform),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            section.PropertyChanged += (_, e) =>
            {
                if (e.Property != Expander.IsExpandedProperty) return;
                if (section.IsExpanded) _expandedVideoSections.Add(platform);
                else _expandedVideoSections.Remove(platform);
            };
            VideoPanel.Children.Add(section);
        }

        VideoPanel.Children.Add(new TextBlock
        {
            Text = "Hinweis: Echte Grafik-Shader und eine höhere interne Auflösung (z. B. DS in 2x) brauchen die " +
                   "Grafikkarte. Das kommt zusammen mit N64, GameCube und 3DS.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.6,
        });
    }

    /// <summary>Die Einstellungen, die der Emulator selbst anbietet (z. B. Farbkorrektur).</summary>
    private Expander BuildCoreOptionsExpander(string coreName)
    {
        var panel = new StackPanel { Spacing = 6 };
        var definitions = CoreOptionStore.Load(coreName);

        if (definitions.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Starte einmal ein Spiel dieser Konsole – dann erscheinen hier die Einstellungen des Emulators.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
            });
        }
        else
        {
            var current = _settings.CoreOptions.GetValueOrDefault(coreName) ?? [];
            foreach (var def in definitions.Where(d => d.Values.Length >= 2))
            {
                var values = def.Values;
                int index = Math.Max(0, Array.IndexOf(values, current.GetValueOrDefault(def.Key, values[0])));
                var box = new ComboBox { ItemsSource = values, SelectedIndex = index, Width = 260 };
                box.SelectionChanged += (_, _) =>
                {
                    if (box.SelectedIndex >= 0) SetCoreOption(coreName, def.Key, values[box.SelectedIndex]);
                };
                panel.Children.Add(LabeledRow(def.Description, box, labelWidth: 380));
            }

            var reset = new Button { Content = "Emulator-Optionen zurücksetzen" };
            reset.Click += (_, _) =>
            {
                _settings.CoreOptions.Remove(coreName);
                _settings.Save();
                SetStatus("Emulator-Optionen zurückgesetzt – wirkt beim nächsten Spielstart.");
                BuildVideoPanel();
            };
            panel.Children.Add(reset);
            panel.Children.Add(new TextBlock
            {
                Text = "Manche Optionen wirken erst nach einem Neustart des Spiels.",
                Opacity = 0.6,
            });
        }

        return new Expander
        {
            Header = $"Emulator-Optionen ({coreName.Replace("_libretro", "")})",
            Content = panel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
    }

    private void SetCoreOption(string coreName, string key, string value)
    {
        if (!_settings.CoreOptions.TryGetValue(coreName, out var options))
            _settings.CoreOptions[coreName] = options = [];
        options[key] = value;
        _settings.Save();

        if (_runningGame?.Session.CoreName == coreName)
            _runningGame.Session.RequestOptionChange(key, value);
    }

    private static Grid LabeledRow(string label, Control control, int labelWidth = 200)
    {
        Grid.SetColumn(control, 1);
        control.VerticalAlignment = VerticalAlignment.Center;
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"{labelWidth},*"),
            Children =
            {
                new TextBlock
                {
                    Text = label,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 10, 0),
                },
                control,
            },
        };
    }

    private ComboBox Choice(string[] labels, int selected, Action<int> onChange)
    {
        var box = new ComboBox { ItemsSource = labels, SelectedIndex = selected, Width = 300 };
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex < 0) return;
            onChange(box.SelectedIndex);
            SaveAndApply();
        };
        return box;
    }

    // =====================================================================
    // Steuerung belegen
    // =====================================================================

    private Action<string>? _keyCapture;
    private DispatcherTimer? _padCaptureTimer;

    private void BuildControlsPanel()
    {
        StopPadCapture();
        _keyCapture = null;
        var input = _settings.Input;
        ControlsPanel.Children.Clear();

        ControlsPanel.Children.Add(new TextBlock
        {
            Text = "Auf eine Belegung klicken und dann die gewünschte Taste drücken. Esc bricht ab, ✕ löscht eine Belegung. " +
                   "Änderungen gelten sofort, auch im laufenden Spiel.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
        });

        var names = SdlGamepad.Instance.ConnectedNames();
        ControlsPanel.Children.Add(new TextBlock
        {
            Text = names.Count > 0 ? "🎮 Verbunden: " + string.Join(", ", names)
                : SdlGamepad.Instance.InitError is { } err ? "🎮 Controller-Unterstützung nicht verfügbar: " + err
                : SdlGamepad.Instance.UnmappedDevices.Count > 0
                    ? "🎮 Gerät gefunden, aber ohne Tastenzuordnung: " + string.Join(", ", SdlGamepad.Instance.UnmappedDevices)
                : "🎮 Kein Controller gefunden. Anschließen und auf „Controller suchen“ klicken.",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold,
        });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("170,200,40,250,40") };
        int row = 0;

        void AddCell(Control control, int column)
        {
            Grid.SetRow(control, row);
            Grid.SetColumn(control, column);
            control.Margin = new Thickness(0, 3, 10, 3);
            grid.Children.Add(control);
        }

        void AddRow(string label, string keyText, Action<string> setKey, string padText, Action<PadControl> setPad)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            AddCell(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }, 0);

            var keyButton = new Button { Content = keyText, HorizontalAlignment = HorizontalAlignment.Stretch };
            keyButton.Click += (_, _) => StartKeyCapture(keyButton, setKey);
            AddCell(keyButton, 1);
            AddCell(ClearButton("Tastatur-Belegung löschen", () => setKey("")), 2);

            var padButton = new Button { Content = padText, HorizontalAlignment = HorizontalAlignment.Stretch };
            padButton.Click += (_, _) => StartPadCapture(padButton, setPad);
            AddCell(padButton, 3);
            AddCell(ClearButton("Controller-Belegung löschen", () => setPad(PadControl.None)), 4);
            row++;
        }

        // Kopfzeile
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        AddCell(new TextBlock { Text = "Spieltaste", FontWeight = FontWeight.SemiBold }, 0);
        AddCell(new TextBlock { Text = "Tastatur", FontWeight = FontWeight.SemiBold }, 1);
        AddCell(new TextBlock { Text = "Controller", FontWeight = FontWeight.SemiBold }, 3);
        row++;

        // Neue Wörterbücher statt Änderungen an den alten: der Emulator-Thread liest gleichzeitig mit
        foreach (var (button, label) in InputSettings.ButtonLabels)
        {
            AddRow(label,
                InputSettings.KeyLabel(input.Keyboard.GetValueOrDefault(button, "")),
                key => { ClearKey(key); input.Keyboard = new(input.Keyboard) { [button] = key }; },
                InputSettings.PadLabel(input.Gamepad.GetValueOrDefault(button, PadControl.None)),
                pad => input.Gamepad = new(input.Gamepad) { [button] = pad });
        }

        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            AddRow(InputSettings.HotkeyLabel(action),
                InputSettings.KeyLabel(input.HotkeyKeyboard.GetValueOrDefault(action, "")),
                key => { ClearKey(key); input.HotkeyKeyboard = new(input.HotkeyKeyboard) { [action] = key }; },
                InputSettings.PadLabel(input.HotkeyGamepad.GetValueOrDefault(action, PadControl.None)),
                pad => input.HotkeyGamepad = new(input.HotkeyGamepad) { [action] = pad });
        }

        ControlsPanel.Children.Add(grid);

        var stickBox = new CheckBox
        {
            Content = "Linker Stick bewegt zusätzlich das Steuerkreuz",
            IsChecked = input.LeftStickAsDpad,
        };
        stickBox.IsCheckedChanged += (_, _) =>
        {
            input.LeftStickAsDpad = stickBox.IsChecked == true;
            _settings.Save();
        };
        ControlsPanel.Children.Add(stickBox);

        var resetButton = new Button { Content = "Standardbelegung wiederherstellen" };
        resetButton.Click += (_, _) =>
        {
            input.ResetToDefaults();
            _settings.Save();
            BuildControlsPanel();
        };
        var rescanButton = new Button { Content = "Controller suchen" };
        rescanButton.Click += (_, _) => BuildControlsPanel();

        ControlsPanel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { resetButton, rescanButton },
        });

        ControlsPanel.Children.Add(new TextBlock
        {
            Text = "Feste Tasten im Spiel: P = Pause · F11 = Vollbild · Esc = Vollbild verlassen",
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        });
    }

    /// <summary>Kleiner ✕-Knopf zum Löschen einer Belegung.</summary>
    private Button ClearButton(string tooltip, Action clear)
    {
        var button = new Button { Content = "✕", HorizontalContentAlignment = HorizontalAlignment.Center };
        ToolTip.SetTip(button, tooltip);
        button.Click += (_, _) =>
        {
            clear();
            _settings.Save();
            BuildControlsPanel();
        };
        return button;
    }

    /// <summary>Eine Taste darf nur einmal belegt sein: alte Belegung entfernen.</summary>
    private void ClearKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        var input = _settings.Input;
        input.Keyboard = input.Keyboard.ToDictionary(p => p.Key, p => p.Value == key ? "" : p.Value);
        input.HotkeyKeyboard = input.HotkeyKeyboard.ToDictionary(p => p.Key, p => p.Value == key ? "" : p.Value);
    }

    private void StartKeyCapture(Button button, Action<string> setKey)
    {
        StopPadCapture();
        button.Content = "Taste drücken…";
        _keyCapture = key =>
        {
            if (key == nameof(Key.Delete)) setKey("");
            else if (key != nameof(Key.Escape)) setKey(key);
            _settings.Save();
            BuildControlsPanel();
        };
    }

    private void OnCaptureKeyDown(object? sender, KeyEventArgs e)
    {
        if (_keyCapture is null) return;
        var capture = _keyCapture;
        _keyCapture = null;
        e.Handled = true;
        capture(e.Key.ToString());
    }

    private void StartPadCapture(Button button, Action<PadControl> setPad)
    {
        StopPadCapture();
        _keyCapture = null;
        button.Content = "Controller-Taste drücken…";

        bool released = false;
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) =>
        {
            var pressed = SdlGamepad.Instance.FirstPressed();
            if (!released)
            {
                released = pressed == PadControl.None; // erst warten, bis alles losgelassen ist
            }
            else if (pressed != PadControl.None)
            {
                StopPadCapture();
                setPad(pressed);
                _settings.Save();
                BuildControlsPanel();
                return;
            }

            if ((DateTime.UtcNow - started).TotalSeconds > 6)
            {
                StopPadCapture();
                BuildControlsPanel(); // Zeit abgelaufen -> nichts ändern
            }
        };
        _padCaptureTimer = timer;
        timer.Start();
    }

    private void StopPadCapture()
    {
        _padCaptureTimer?.Stop();
        _padCaptureTimer = null;
    }

    private async void OnSaveSettingsClick(object? sender, RoutedEventArgs e)
    {
        _settings.RomFolder = RomFolderBox.Text?.Trim() ?? "";
        _settings.Save();
        await ScanLibraryAsync();
    }

    private async void OnBrowseRomFolderClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "ROM-Ordner wählen",
            AllowMultiple = false,
        });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path) return;

        RomFolderBox.Text = path;
        _settings.RomFolder = path;
        _settings.Save();
        await ScanLibraryAsync();
    }

    // =====================================================================
    // Hilfsfunktionen
    // =====================================================================

    private void SetStatus(string text) => StatusText.Text = text;

    /// <summary>Einfaches Meldungsfenster (Avalonia bringt keine MessageBox mit).</summary>
    private async Task ShowMessageAsync(string title, string message)
    {
        var ok = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 80 };
        var dialog = new Window
        {
            Title = title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    ok,
                },
            },
        };
        ok.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }
}
