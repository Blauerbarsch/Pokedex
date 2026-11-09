using System.Diagnostics;
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
using DexHub.Core.Pokemon;
using DexHub.Core.Saves;
using DexHub.Core.Settings;
using DexHub.Core.Trading;
using GamePlatform = DexHub.Core.Models.Platform;

namespace DexHub.App;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly PokeApiRepository _pokeApi = new();

    private List<GameEntry> _games = [];
    private List<PokemonSpecies> _species = [];
    private List<DexEntry> _dex = [];

    private CancellationTokenSource? _spriteCts;
    private bool _ready; // verhindert, dass Events beim Laden der Oberfläche zu früh feuern

    public MainWindow()
    {
        InitializeComponent();
        FillSettingsForm();
        FillProfiles();
        _ready = true;

        // Tastendrücke abfangen, bevor ein Steuerelement sie bekommt (für "Taste belegen")
        AddHandler(KeyDownEvent, OnCaptureKeyDown, RoutingStrategies.Tunnel);

        WatchSaveFolder();
        InitUpdates();
        InitBackup();

        if (Program.StartInBigPicture)
            Opened += async (_, _) =>
            {
                await ScanLibraryAsync();
                OpenBigPicture();
            };

        Opened += async (_, _) =>
        {
            await ScanLibraryAsync();
            await LoadPokedexAsync(forceRefresh: false);
        };

        // Läuft beim Schließen noch ein Spiel, dieses zuerst sauber beenden (Spielstand speichern)
        // Spiele laufen als eigene Prozesse weiter, auch wenn das Hauptfenster geschlossen wird
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
        BuildTradePanel();
        BuildBankPanel();
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
                        Text = game.StatusText + PlayTimeText(_settings.CurrentProfile, game),
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
        var menu = new List<object> { play, fresh };
        if (game.Info.Platform is GamePlatform.GameBoy or GamePlatform.GameBoyColor)
        {
            var host = new MenuItem { Header = "🔗 Linkkabel: als Gastgeber starten" };
            host.Click += async (_, _) => await LaunchWithLinkAsync(game, host: true);
            var join = new MenuItem { Header = "🔗 Linkkabel: mit Gastgeber verbinden" };
            join.Click += async (_, _) => await LaunchWithLinkAsync(game, host: false);
            menu.Add(new Separator());
            menu.Add(host);
            menu.Add(join);
        }
        menu.Add(new Separator());
        menu.Add(info);
        tile.ContextMenu = new ContextMenu { ItemsSource = menu };

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

    /// <summary>" · ⏱ 3 Std. 12 Min." – leer, wenn noch nie gespielt.</summary>
    private static string PlayTimeText(string profile, GameEntry game) =>
        DexHub.Core.Stats.PlayTimeStore.Get(profile, game.RomPath) is { Seconds: >= 60 } entry
            ? " · ⏱ " + DexHub.Core.Stats.PlayTimeStore.Format(TimeSpan.FromSeconds(entry.Seconds))
            : "";

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

    /// <summary>Laufende Spiele (jedes in einem eigenen Prozess).</summary>
    private sealed record RunningGame(GameEntry Game, string Profile, Process Process);
    private readonly List<RunningGame> _running = [];

    private bool AnyGameRunning => _running.Any(r => !r.Process.HasExited);

    private bool IsRunning(GameEntry game, string profile) =>
        _running.Any(r => !r.Process.HasExited && r.Game.RomPath == game.RomPath && r.Profile == profile);

    private async Task LaunchGameAsync(GameEntry game, bool resume = true, Dictionary<string, string>? options = null,
        bool fullscreen = false)
    {
        var profile = _settings.CurrentProfile;
        if (IsRunning(game, profile))
        {
            await ShowMessageAsync("Läuft bereits",
                $"{game.Title} läuft für {profile} schon. Für ein zweites Fenster oben einen anderen Benutzer wählen – " +
                "zwei Fenster mit demselben Spielstand würden sich gegenseitig überschreiben.");
            return;
        }

        var platform = game.Info.Platform;
        if (!CoreCatalog.IsPlatformSupported(platform))
        {
            await ShowMessageAsync("Noch nicht spielbar",
                $"{platform.DisplayName()}-Spiele werden in einem späteren Schritt eingebaut.");
            return;
        }

        if (CoreCatalog.FindCore(platform) is null)
        {
            await ShowMessageAsync("Emulator fehlt",
                $"Der Emulator für {platform.DisplayName()} ({CoreCatalog.Cores[platform]}) wurde nicht gefunden.\n\n" +
                "Beim Entwickeln einmal im Projektordner ausführen:\n  dotnet run scripts/FetchCores.cs");
            return;
        }

        Process process;
        try
        {
            process = Process.Start(PlayerHost.CreateStartInfo(game.RomPath, profile, resume && _settings.AutoResume, options, fullscreen))
                      ?? throw new InvalidOperationException("Prozess konnte nicht gestartet werden.");
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Spiel konnte nicht gestartet werden", ex.Message);
            return;
        }

        var running = new RunningGame(game, profile, process);
        _running.Add(running);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            _running.Remove(running);
            BuildVideoPanel(); // jetzt sind evtl. neue Emulator-Optionen bekannt
            foreach (var g in _games) g.SavePath = SaveLocator.Find(g);
            RefreshGamesList();
            RebuildDex();
            SetStatus($"{game.Title} ({running.Profile}) beendet.");
        });

        SetStatus($"{game.Title} läuft für {profile}…");
    }

    /// <summary>
    /// Linkkabel für Game Boy/Color: Gambatte kann zwei Spiele über das Netzwerk verbinden.
    /// Das erste Fenster ist Gastgeber, das zweite verbindet sich mit 127.0.0.1 (derselbe PC).
    /// Die genauen Options-Namen liest DexHub aus dem Core, sobald einmal ein GB-Spiel lief.
    /// </summary>
    private Dictionary<string, string>? GameBoyLinkOptions(bool host, out string error)
    {
        error = "";
        var definitions = CoreOptionStore.Load("gambatte_libretro");
        var options = new Dictionary<string, string>();

        if (definitions.Count == 0)
        {
            // Noch nie ein GB-Spiel gestartet: Standardnamen des Gambatte-Cores verwenden
            options["gambatte_gb_link_mode"] = host ? "Network Server" : "Network Client";
            if (!host)
            {
                options["gambatte_gb_link_network_server_ip_octet1"] = "127";
                options["gambatte_gb_link_network_server_ip_octet2"] = "0";
                options["gambatte_gb_link_network_server_ip_octet3"] = "0";
                options["gambatte_gb_link_network_server_ip_octet4"] = "1";
            }
            return options;
        }

        var mode = definitions.FirstOrDefault(d => d.Key.Contains("link_mode", StringComparison.OrdinalIgnoreCase));
        var value = mode?.Values.FirstOrDefault(v => v.Contains(host ? "Server" : "Client", StringComparison.OrdinalIgnoreCase));
        if (mode is null || value is null)
        {
            error = "Dieser Game-Boy-Emulator wurde ohne Linkkabel-Unterstützung gebaut.";
            return null;
        }
        options[mode.Key] = value;

        if (!host)
        {
            string[] localhost = ["127", "0", "0", "1"];
            foreach (var def in definitions.Where(d => d.Key.Contains("ip_octet", StringComparison.OrdinalIgnoreCase)))
                if (int.TryParse(def.Key[^1..], out var n) && n is >= 1 and <= 4)
                    options[def.Key] = localhost[n - 1];
        }
        return options;
    }

    private async Task LaunchWithLinkAsync(GameEntry game, bool host)
    {
        var options = GameBoyLinkOptions(host, out var error);
        if (options is null)
        {
            await ShowMessageAsync("Linkkabel", error);
            return;
        }
        await LaunchGameAsync(game, resume: true, options);
        SetStatus(host
            ? $"Linkkabel-Gastgeber gestartet ({_settings.CurrentProfile}). Jetzt oben einen anderen Benutzer wählen und „Linkkabel: verbinden“ starten."
            : $"Verbinde mit dem Gastgeber ({_settings.CurrentProfile})… Im Spiel dann im Pokémon-Center zum Kabel-Club gehen.");
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
            // Ganzen DexHub-Ordner beobachten (alle Benutzer), aber nur auf Spielstände reagieren
            _saveWatcher = new FileSystemWatcher(AppSettings.AppDataFolder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            FileSystemEventHandler changed = (_, e) =>
            {
                var path = e.FullPath.Replace('\\', '/');
                if (path.EndsWith("/hunts.json", StringComparison.Ordinal))
                {
                    Dispatcher.UIThread.Post(RebuildHunts);
                    return;
                }
                if (!path.Contains("/saves/") && !path.EndsWith("/saves", StringComparison.Ordinal)) return;
                Dispatcher.UIThread.Post(OnSaveFolderChanged);
            };
            void OnSaveFolderChanged()
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
            }
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
            try
            {
                _gameData = await _pokeApi.LoadGameDataAsync(forceRefresh);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[DexHub] Spieldaten (Attacken, Wesen) nicht ladbar: " + ex.Message);
            }
            RebuildDex();
            if (_tradeA is not null) LoadTradeColumn(_tradeA);
            if (_tradeB is not null) LoadTradeColumn(_tradeB);
            BuildBankPanel();
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
        _collection = CollectionService.Load(_games, _gameData);
        if (_gameData is not null)
            foreach (var banked in PokemonBank.Load(_settings.CurrentProfile))
                if (banked.Parse(_gameData) is { } p)
                {
                    p.Level = _gameData.LevelFromExp(p.Species, p.Exp);
                    _collection.Add(p);
                }
        BuildStats();

        // Gruppen nur einmal anlegen (bzw. wenn sich die Anzahl Generationen ändert)
        var generations = _dex.Select(d => d.Species.Generation).Distinct().Order().ToList();
        if (!generations.SequenceEqual(_dexGroups.Select(g => g.Generation)))
            BuildDexGroups(generations);

        foreach (var (id, tile) in _dexTiles)
            if (_dexById.TryGetValue(id, out var entry)) StyleDexTile(tile, entry);

        RecomputeObtainable();
        ApplyDexFilter();

        // Detailansicht aktuell halten
        if (_dexById.TryGetValue(_selectedDexId, out var selected))
        {
            DetailText.Text = selected.DetailText + OwnedSummary(_selectedDexId);
            ShowWhereToFind(_selectedDexId);
        }
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
        int show = DexShowBox.SelectedIndex; // 0 alle, 1 gefangen, 2 fehlend, 3 fehlend + erhältlich

        bool Matches(DexEntry d) =>
            (query.Length == 0 || d.Id == number
                || d.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || d.Species.NameEn.Contains(query, StringComparison.OrdinalIgnoreCase))
            && (genFilter <= 0 || d.Species.Generation == genFilter)
            && show switch
            {
                1 => d.IsCaught,
                2 => !d.IsCaught,
                3 => !d.IsCaught && _obtainable.ContainsKey(d.Id),
                _ => true,
            };

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

    private async void OnReloadDexClick(object? sender, RoutedEventArgs e) => await LoadPokedexAsync(forceRefresh: true);

    private async Task ShowDexDetailAsync(int id)
    {
        if (!_dexById.TryGetValue(id, out var entry)) return;

        int previous = _selectedDexId;
        _selectedDexId = id;
        foreach (var other in new[] { previous, id })
            if (_dexTiles.TryGetValue(other, out var t) && _dexById.TryGetValue(other, out var e)) StyleDexTile(t, e);

        DetailName.Text = $"{entry.Number} {entry.Name}";
        OpenInGuideButton.IsVisible = true;
        DetailText.Text = entry.DetailText + OwnedSummary(id);
        ShowWhereToFind(id);
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
    // Statistik, eigene Pokémon und Rückblick
    // =====================================================================

    private GameData? _gameData;
    private List<OwnedPokemon> _collection = [];

    private string SpeciesName(int id) => _dexById.TryGetValue(id, out var e) ? e.Name : $"#{id}";

    /// <summary>
    /// Spitzname oder Artname. Gen 1–3 speichern immer einen Namen – entspricht er dem Artnamen
    /// (z. B. "GLUMANDA" oder englisch "CHARMANDER"), ist es kein echter Spitzname.
    /// </summary>
    private string NickOrName(OwnedPokemon p)
    {
        if (p.IsEgg) return "Ei";
        var nick = p.Nickname?.Trim();
        if (string.IsNullOrEmpty(nick)) return SpeciesName(p.Species);
        bool isSpeciesName = string.Equals(nick, SpeciesName(p.Species), StringComparison.OrdinalIgnoreCase)
            || (_dexById.TryGetValue(p.Species, out var e) && string.Equals(nick, e.Species.NameEn, StringComparison.OrdinalIgnoreCase));
        return isSpeciesName ? SpeciesName(p.Species) : nick;
    }

    private string DisplayName(OwnedPokemon p) => NickOrName(p) is var name && name != SpeciesName(p.Species) && !p.IsEgg
        ? $"{name} ({SpeciesName(p.Species)})"
        : name;

    /// <summary>Für die Pokédex-Details: alle eigenen Exemplare dieser Art.</summary>
    private string OwnedSummary(int species)
    {
        var own = _collection.Where(p => p.Species == species && !p.IsEgg).OrderByDescending(p => p.Level).ToList();
        if (own.Count == 0) return "";
        var lines = own.Select(p =>
            $"• Lv. {p.Level} {NickOrName(p)} {p.Gender}{(p.IsShiny ? " ✨" : "")} – {p.GameTitle}, {p.Location}");
        return $"\n\nDeine Exemplare ({own.Count}):\n" + string.Join("\n", lines);
    }

    private void BuildStats()
    {
        StatsPanel.Children.Clear();
        var mons = _collection.Where(p => !p.IsEgg).ToList();

        StatsPanel.Children.Add(StatsSection("Überblick", BuildOverview(mons), expanded: true));
        StatsPanel.Children.Add(BuildAchievementsSection(mons));
        StatsPanel.Children.Add(StatsSection($"Meine Pokémon ({mons.Count})", BuildMyPokemon(mons), expanded: true));
        StatsPanel.Children.Add(StatsSection("Rückblick", BuildTimeline(mons), expanded: false));
        _huntPanel = new StackPanel { Spacing = 10 };
        StatsPanel.Children.Add(StatsSection("✨ Shiny-Jagden", _huntPanel, expanded: true));
        RebuildHunts();
    }

    private static Expander StatsSection(string title, Control content, bool expanded) => new()
    {
        Header = title,
        Content = content,
        IsExpanded = expanded,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    private static TextBlock Text(string text, double size = 14, bool bold = false, double opacity = 1) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
        Opacity = opacity,
        TextWrapping = TextWrapping.Wrap,
    };

    private static Control StatCard(string value, string label) => new Border
    {
        Background = TileBrush,
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(14, 10),
        Margin = new Thickness(0, 0, 8, 8),
        MinWidth = 150,
        Child = new StackPanel { Children = { Text(value, 26, bold: true), Text(label, 12, opacity: 0.7) } },
    };

    private Control BuildOverview(List<OwnedPokemon> mons)
    {
        var panel = new StackPanel { Spacing = 12 };
        int caughtSpecies = _dex.Count(d => d.IsCaught);

        panel.Children.Add(new WrapPanel
        {
            Children =
            {
                StatCard($"{caughtSpecies} / {_dex.Count}", "Arten gefangen (alle Spiele)"),
                StatCard($"{_dex.Count(d => d.SeenIn.Count > 0 || d.IsCaught)}", "Arten gesehen"),
                StatCard($"{mons.Count}", "Pokémon in Team & Boxen"),
                StatCard($"{mons.Count(p => p.IsShiny)}", "Schillernde ✨"),
                StatCard(DexHub.Core.Stats.PlayTimeStore.Format(DexHub.Core.Stats.PlayTimeStore.Total(_settings.CurrentProfile)),
                    $"Spielzeit von {_settings.CurrentProfile}"),
            },
        });

        // Fortschritt pro Generation
        panel.Children.Add(Text("Fortschritt pro Generation", bold: true));
        foreach (var gen in _dex.GroupBy(d => d.Species.Generation).OrderBy(g => g.Key))
        {
            int total = gen.Count(), caught = gen.Count(d => d.IsCaught);
            panel.Children.Add(ProgressRow($"Gen {gen.Key} – {Regions.ElementAtOrDefault(gen.Key)}", caught, total));
        }

        // Fortschritt pro Spiel
        var games = _games.Where(g => g.SavePath is not null && g.Info.SaveFormat != SaveFormat.None).ToList();
        if (games.Count > 0)
        {
            panel.Children.Add(Text("Pokédex pro Spiel", bold: true));
            foreach (var game in games)
            {
                var progress = SaveReader.Read(game.SavePath!, game.Info.SaveFormat);
                if (progress is null) continue;
                int max = game.Info.Generation switch { 1 => 151, 2 => 251, 3 => 386, 4 => 493, 5 => 649, _ => _dex.Count };
                panel.Children.Add(ProgressRow(game.Title, progress.Caught.Count, max, $" · {progress.Seen.Count} gesehen"));
            }
        }

        // Highlights
        if (mons.Count > 0)
        {
            panel.Children.Add(Text("Highlights", bold: true));
            var strongest = mons.MaxBy(p => p.Level)!;
            panel.Children.Add(Text($"Höchstes Level: {DisplayName(strongest)} mit Lv. {strongest.Level} ({strongest.GameTitle})"));

            var favorites = mons.GroupBy(p => p.Species)
                .Where(g => g.Count() > 1)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .Select(g => $"{SpeciesName(g.Key)} ×{g.Count()}")
                .ToList();
            if (favorites.Count > 0) panel.Children.Add(Text("Am häufigsten: " + string.Join(", ", favorites)));

            var balls = mons.GroupBy(p => p.Ball).OrderByDescending(g => g.Count()).Take(3)
                .Select(g => $"{GameData.Ball(g.Key)} ×{g.Count()}");
            panel.Children.Add(Text("Lieblingsbälle: " + string.Join(", ", balls)));

            var perfect = mons.Where(p => p.Ivs.Length == 6 && p.Ivs.Count(iv => iv == 31) >= 3).ToList();
            if (perfect.Count > 0)
                panel.Children.Add(Text($"Mit mindestens 3 perfekten IVs: {string.Join(", ", perfect.Select(DisplayName))}"));
        }
        else
        {
            panel.Children.Add(Text("Noch keine Pokémon in den Spielständen gefunden (Gen 1–4 werden gelesen).", opacity: 0.6));
        }

        return panel;
    }

    private static Control ProgressRow(string label, int value, int max, string extra = "")
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*,190") };
        grid.Children.Add(Text(label));
        grid.Children.Add(WithColumn(new ProgressBar
        {
            Minimum = 0,
            Maximum = Math.Max(1, max),
            Value = value,
            Height = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0),
        }, 1));
        var percent = max > 0 ? 100.0 * value / max : 0;
        grid.Children.Add(WithColumn(Text($"{value} / {max} ({percent:0} %){extra}", 12, opacity: 0.8), 2));
        return grid;
    }

    private Control BuildMyPokemon(List<OwnedPokemon> mons)
    {
        var panel = new StackPanel { Spacing = 6 };
        if (mons.Count == 0)
        {
            panel.Children.Add(Text("Noch keine Pokémon gefunden. Spiele ein Spiel aus Gen 1–4 und speichere im Spiel.", opacity: 0.6));
            return panel;
        }

        foreach (var game in mons.GroupBy(p => p.GameTitle).OrderBy(g => g.Key))
        {
            int team = game.Count(p => p.Location == "Team");
            var wrap = new WrapPanel();
            var ordered = game
                .OrderBy(p => p.Location == "Team" ? 0 : int.TryParse(p.Location.Replace("Box ", ""), out var b) ? b : 99)
                .ThenBy(p => p.Species);
            foreach (var p in ordered) wrap.Children.Add(CreatePokemonTile(p));

            panel.Children.Add(new Expander
            {
                Header = $"{game.Key} – {team} im Team, {game.Count() - team} in Boxen",
                Content = wrap,
                IsExpanded = true,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            });
        }
        return panel;
    }

    private Border CreatePokemonTile(OwnedPokemon p)
    {
        var sprite = new Image { Width = 80, Height = 80, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapInterpolationMode(sprite, BitmapInterpolationMode.None);

        var tile = new Border
        {
            Width = 124,
            Margin = new Thickness(4),
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(8),
            Background = TileBrush,
            BorderThickness = new Thickness(2),
            BorderBrush = p.IsShiny ? new SolidColorBrush(Color.Parse("#F5C542")) : Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                        Children =
                        {
                            Text($"Lv. {p.Level}", 12, opacity: 0.7),
                            WithColumn(Text(p.Gender + (p.IsShiny ? " ✨" : ""), 12), 1),
                        },
                    },
                    sprite,
                    new TextBlock
                    {
                        Text = NickOrName(p),
                        FontWeight = FontWeight.SemiBold,
                        TextAlignment = TextAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                    new TextBlock { Text = p.Location, FontSize = 11, Opacity = 0.6, TextAlignment = TextAlignment.Center },
                },
            },
        };
        tile.PointerPressed += async (_, _) => await ShowPokemonDetailAsync(p);
        _ = LoadOwnedSpriteAsync(p, sprite);
        return tile;
    }

    private async Task LoadOwnedSpriteAsync(OwnedPokemon p, Image target)
    {
        var path = await _pokeApi.GetSpritePathAsync(p.Species, shiny: p.IsShiny)
                   ?? await _pokeApi.GetSpritePathAsync(p.Species);
        if (path is null) return;
        try { target.Source = new Bitmap(path); }
        catch (Exception ex) { Console.WriteLine($"[DexHub] Bild #{p.Species} fehlerhaft: {ex.Message}"); }
    }

    private async Task ShowPokemonDetailAsync(OwnedPokemon p)
    {
        var panel = PokemonDetailPanel;
        panel.Children.Clear();

        var sprite = new Image { Width = 160, Height = 160, HorizontalAlignment = HorizontalAlignment.Center };
        RenderOptions.SetBitmapInterpolationMode(sprite, BitmapInterpolationMode.None);
        panel.Children.Add(sprite);

        panel.Children.Add(Text(NickOrName(p), 22, bold: true));
        panel.Children.Add(Text($"#{p.Species:0000} {SpeciesName(p.Species)} {p.Gender}{(p.IsShiny ? "  ✨ schillernd" : "")}", opacity: 0.8));
        panel.Children.Add(Text(p.HasNature
            ? $"Level {p.Level} · Wesen: {_gameData?.Nature(p.Nature) ?? p.Nature.ToString()}"
            : $"Level {p.Level} · Gen {p.Generation}"));
        panel.Children.Add(Text($"{p.GameTitle} · {p.Location}", opacity: 0.8));

        if (p.Generation >= 3)
        {
            var met = p.MetDate is { } date ? $"Gefangen am {date:dd.MM.yyyy}" : "Fangdatum: erst ab Gen 4 gespeichert";
            panel.Children.Add(Text($"{met} · mit Lv. {p.MetLevel} im {GameData.Ball(p.Ball)}"));
        }
        panel.Children.Add(Text($"Trainer: {p.TrainerName} (ID {p.TrainerId:00000}) · Freundschaft {p.Friendship}/255", 12, opacity: 0.7));

        panel.Children.Add(Text("Attacken", bold: true));
        foreach (var move in p.Moves.Where(m => m != 0))
            panel.Children.Add(Text("• " + (_gameData?.Move(move) ?? $"Attacke #{move}")));

        panel.Children.Add(Text("Werte (IV / EV)", bold: true));
        for (int i = 0; i < 6 && i < p.Ivs.Length; i++)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("80,*,70") };
            row.Children.Add(Text(GameData.StatNames[i], 12));
            row.Children.Add(WithColumn(new ProgressBar
            {
                Minimum = 0, Maximum = p.Generation <= 2 ? 15 : 31, Value = p.Ivs[i], Height = 8,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0),
            }, 1));
            row.Children.Add(WithColumn(Text($"{p.Ivs[i]} / {p.Evs.ElementAtOrDefault(i)}", 12), 2));
            panel.Children.Add(row);
        }
        panel.Children.Add(Text(p.Generation <= 2
            ? "DV: 0–15 (angeboren), Stat-EP: Fleißpunkte (max. 65535), Spezial zählt für Sp.-Angr. und Sp.-Vert."
            : "IV: 0–31 (angeboren), EV: Fleißpunkte (max. 255 pro Wert)", 11, opacity: 0.5));

        var path = await _pokeApi.GetSpritePathAsync(p.Species, shiny: p.IsShiny);
        if (path is not null) sprite.Source = new Bitmap(path);
    }

    private Control BuildTimeline(List<OwnedPokemon> mons)
    {
        var panel = new StackPanel { Spacing = 10 };
        var dated = mons.Where(p => p.MetDate is not null).OrderByDescending(p => p.MetDate).ToList();
        if (dated.Count == 0)
        {
            panel.Children.Add(Text("Noch keine Fangdaten vorhanden.", opacity: 0.6));
            return panel;
        }

        var culture = new System.Globalization.CultureInfo("de-DE");
        var first = dated[^1];
        panel.Children.Add(Text($"Erster Fang: {DisplayName(first)} am {first.MetDate:dd.MM.yyyy} ({first.GameTitle})", opacity: 0.8));

        // Pro Monat zusammenfassen, darin pro Tag
        foreach (var month in dated.GroupBy(p => new DateOnly(p.MetDate!.Value.Year, p.MetDate.Value.Month, 1)).Take(24))
        {
            var monthPanel = new StackPanel { Spacing = 6 };
            foreach (var day in month.GroupBy(p => p.MetDate!.Value))
            {
                monthPanel.Children.Add(Text(day.Key.ToString("dddd, d. MMMM", culture), 13, bold: true));
                foreach (var p in day)
                    monthPanel.Children.Add(Text(
                        $"   {DisplayName(p)}{(p.IsShiny ? " ✨" : "")} – mit Lv. {p.MetLevel} im {GameData.Ball(p.Ball)}, heute Lv. {p.Level} · {p.GameTitle}",
                        13));
            }

            panel.Children.Add(new Expander
            {
                Header = $"{month.Key.ToString("MMMM yyyy", culture)} – {month.Count()} Pokémon",
                Content = monthPanel,
                IsExpanded = month.Key == new DateOnly(dated[0].MetDate!.Value.Year, dated[0].MetDate!.Value.Month, 1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            });
        }
        return panel;
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
        // Laufende Spiele übernehmen Änderungen selbst (sie beobachten settings.json)
    }

    // =====================================================================
    // Tauschzentrale
    // =====================================================================

    /// <summary>Eine Seite der Tauschzentrale: Benutzer, Spiel und die Pokémon in dessen Boxen.</summary>
    private sealed class TradeColumn
    {
        public required ComboBox ProfileBox { get; init; }
        public required ComboBox GameBox { get; init; }
        public required WrapPanel Tiles { get; init; }
        public required TextBlock Info { get; init; }
        public Dictionary<TradeSlot, Border> TileBySlot { get; } = [];
        public TradeSlot? Selected { get; set; }

        public TradeSide? Side =>
            ProfileBox.SelectedItem is string profile && GameBox.SelectedItem is GameChoice game
                ? new TradeSide(profile, game.Game)
                : null;
    }

    /// <summary>Eintrag in der Spielauswahl (ToString für die Anzeige).</summary>
    private sealed record GameChoice(GameEntry Game)
    {
        public override string ToString() => $"{Game.Title}  ({Game.FileName})";
    }

    private TradeColumn? _tradeA, _tradeB;
    private CheckBox? _tradeEvolutionBox;
    private TextBlock? _tradeResult;

    private void BuildTradePanel()
    {
        TradePanel.Children.Clear();
        TradePanel.Children.Add(Text(
            "Tausche Pokémon zwischen zwei Benutzern oder zwischen zwei eigenen Spielen. Links und rechts je ein " +
            "Pokémon aus den PC-Boxen anklicken, dann tauschen. Vorher werden beide Spielstände gesichert. " +
            "Geht für Gen 3 (Rubin bis Blattgrün), Gen 4 (Diamant bis SoulSilver) und Gen 5 (Schwarz/Weiß 1 und 2), jeweils innerhalb einer Generation.", opacity: 0.8));

        var games = _games.Where(g => TradeService.IsSupported(g.Info.SaveFormat))
                          .OrderBy(g => g.Info.Generation).ThenBy(g => g.Title).ToList();
        if (games.Count == 0)
        {
            TradePanel.Children.Add(Text("Keine Gen-3-, Gen-4- oder Gen-5-Spiele in der Bibliothek gefunden.", opacity: 0.6));
            return;
        }

        _tradeA = CreateTradeColumn(games, _settings.CurrentProfile);
        _tradeB = CreateTradeColumn(games,
            _settings.Profiles.FirstOrDefault(p => p != _settings.CurrentProfile) ?? _settings.CurrentProfile);

        _tradeEvolutionBox = new CheckBox { Content = "Tausch-Entwicklungen (z. B. Kadabra → Simsala)", IsChecked = true };
        _tradeResult = Text("", opacity: 0.9);
        var tradeButton = new Button
        {
            Content = "⇄  Tauschen",
            Classes = { "accent" },
            FontSize = 18,
            Padding = new Thickness(24, 10),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        tradeButton.Click += async (_, _) => await ExecuteTradeAsync();

        var middle = new StackPanel
        {
            Spacing = 12,
            Width = 230,
            Margin = new Thickness(12, 40, 12, 0),
            Children = { tradeButton, _tradeEvolutionBox, _tradeResult },
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*") };
        grid.Children.Add(TradeColumnView(_tradeA, "Seite A"));
        grid.Children.Add(WithColumn(middle, 1));
        grid.Children.Add(WithColumn(TradeColumnView(_tradeB, "Seite B"), 2));
        TradePanel.Children.Add(grid);

        LoadTradeColumn(_tradeA);
        LoadTradeColumn(_tradeB);
    }

    private TradeColumn CreateTradeColumn(List<GameEntry> games, string profile)
    {
        var column = new TradeColumn
        {
            ProfileBox = new ComboBox { ItemsSource = _settings.Profiles.ToList(), SelectedItem = profile, MinWidth = 200 },
            GameBox = new ComboBox
            {
                ItemsSource = games.Select(g => new GameChoice(g)).ToList(),
                SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            },
            Tiles = new WrapPanel(),
            Info = Text("", 12, opacity: 0.7),
        };
        column.ProfileBox.SelectionChanged += (_, _) => LoadTradeColumn(column);
        column.GameBox.SelectionChanged += (_, _) => LoadTradeColumn(column);
        return column;
    }

    private static Control TradeColumnView(TradeColumn column, string title) => new Border
    {
        Background = TileBrush,
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(12),
        Child = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Text(title, 16, bold: true),
                LabeledRow("Benutzer", column.ProfileBox, 90),
                LabeledRow("Spiel", column.GameBox, 90),
                column.Info,
                new ScrollViewer { MaxHeight = 560, Content = column.Tiles },
            },
        },
    };

    private void LoadTradeColumn(TradeColumn column)
    {
        column.Tiles.Children.Clear();
        column.TileBySlot.Clear();
        column.Selected = null;

        if (column.Side is not { } side) return;
        if (_gameData is null)
        {
            column.Info.Text = "Spieldaten werden noch geladen …";
            return;
        }
        var save = TradeService.Load(side, _gameData, out var error);
        if (save is null)
        {
            column.Info.Text = error;
            return;
        }

        var mons = save.BoxPokemon();
        column.Info.Text = $"{mons.Count} Pokémon in den Boxen. Eins anklicken.";
        foreach (var (box, slot, pokemon) in mons)
        {
            if (_gameData is not null) pokemon.Level = _gameData.LevelFromExp(pokemon.Species, pokemon.Exp);
            var tradeSlot = new TradeSlot(box, slot, pokemon);
            var tile = CreateTradeTile(pokemon);
            tile.PointerPressed += (_, _) => SelectTradeSlot(column, tradeSlot);
            column.TileBySlot[tradeSlot] = tile;
            column.Tiles.Children.Add(tile);
        }
    }

    private Border CreateTradeTile(OwnedPokemon p)
    {
        var sprite = new Image { Width = 64, Height = 64 };
        RenderOptions.SetBitmapInterpolationMode(sprite, BitmapInterpolationMode.None);
        var tile = new Border
        {
            Width = 104,
            Margin = new Thickness(3),
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(6),
            Background = TileBrush,
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel
            {
                Children =
                {
                    sprite,
                    new TextBlock
                    {
                        Text = (p.IsEgg ? "Ei" : p.Nickname ?? SpeciesName(p.Species)) + (p.IsShiny ? " ✨" : ""),
                        FontWeight = FontWeight.SemiBold,
                        TextAlignment = TextAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                    new TextBlock
                    {
                        Text = $"Lv. {p.Level} {p.Gender} · {p.Location}",
                        FontSize = 11,
                        Opacity = 0.6,
                        TextAlignment = TextAlignment.Center,
                    },
                },
            },
        };
        _ = LoadOwnedSpriteAsync(p, sprite);
        return tile;
    }

    private static void SelectTradeSlot(TradeColumn column, TradeSlot slot)
    {
        column.Selected = slot;
        foreach (var (s, tile) in column.TileBySlot)
            tile.BorderBrush = s == slot ? SelectedBrush : Brushes.Transparent;
        column.Info.Text = $"Ausgewählt: {slot.Pokemon.Nickname ?? "#" + slot.Pokemon.Species} (Lv. {slot.Pokemon.Level}, Box {slot.Box + 1})";
    }

    private async Task ExecuteTradeAsync()
    {
        if (_tradeA?.Side is not { } sideA || _tradeB?.Side is not { } sideB ||
            _tradeA.Selected is not { } slotA || _tradeB.Selected is not { } slotB)
        {
            await ShowMessageAsync("Tauschen", "Bitte links und rechts je ein Pokémon auswählen.");
            return;
        }
        if (IsRunning(sideA.Game, sideA.Profile) || IsRunning(sideB.Game, sideB.Profile))
        {
            await ShowMessageAsync("Tauschen", "Bitte zuerst die beteiligten Spiele beenden – sonst würden sie den Tausch beim Speichern überschreiben.");
            return;
        }

        var question = $"{DisplayName(slotA.Pokemon)} (Lv. {slotA.Pokemon.Level}) von {sideA.Profile} / {sideA.Game.Title}\n" +
                       $"gegen\n{DisplayName(slotB.Pokemon)} (Lv. {slotB.Pokemon.Level}) von {sideB.Profile} / {sideB.Game.Title}\n\n" +
                       "Beide Spielstände werden vorher gesichert. Tauschen?";
        if (!await ConfirmAsync("Tausch bestätigen", question)) return;

        try
        {
            if (_gameData is null) throw new InvalidOperationException("Spieldaten werden noch geladen – bitte kurz warten.");
            if (sideA.Game.Info.Generation != sideB.Game.Info.Generation)
                throw new InvalidOperationException("Getauscht werden kann nur innerhalb einer Generation (Gen 3 ⇄ Gen 3, Gen 4 ⇄ Gen 4, Gen 5 ⇄ Gen 5).");
            var names = new SpeciesNames(SpeciesName,
                id => _dexById.TryGetValue(id, out var e) && e.Species.NameEn.Length > 0 ? e.Species.NameEn : SpeciesName(id));
            var result = TradeService.Trade(sideA, slotA, sideB, slotB,
                _tradeEvolutionBox?.IsChecked == true, names, _gameData);
            if (_tradeResult is not null) _tradeResult.Text = "Getauscht: " + result;
            SetStatus("Tausch abgeschlossen.");
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Tausch fehlgeschlagen", ex.Message + "\n\nEs wurde nichts verändert, falls der Fehler vor dem Speichern auftrat. Sicherungen liegen in:\n" + AppSettings.BackupsFolder);
        }

        // Alles neu einlesen
        foreach (var g in _games) g.SavePath = SaveLocator.Find(g);
        LoadTradeColumn(_tradeA);
        LoadTradeColumn(_tradeB);
        RefreshGamesList();
        RebuildDex();
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        bool result = false;
        var yes = new Button { Content = "Ja", Classes = { "accent" }, MinWidth = 80, IsDefault = true };
        var no = new Button { Content = "Abbrechen", MinWidth = 80, IsCancel = true };
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
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { no, yes },
                    },
                },
            },
        };
        yes.Click += (_, _) => { result = true; dialog.Close(); };
        no.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
        return result;
    }

    // =====================================================================
    // Benutzer
    // =====================================================================

    private void OnBigPictureClick(object? sender, RoutedEventArgs e) => OpenBigPicture();

    private BigPictureWindow? _bigPicture;

    private void OpenBigPicture()
    {
        if (_bigPicture is not null)
        {
            _bigPicture.Activate();
            return;
        }
        _bigPicture = new BigPictureWindow(
            () => _games,
            (game, resume) => LaunchGameAsync(game, resume, fullscreen: true),
            _settings,
            profile =>
            {
                _ready = false;
                ProfileBox.SelectedItem = profile;
                _ready = true;
                SwitchProfile(profile);
            });
        _bigPicture.Closed += (_, _) => _bigPicture = null;
        _bigPicture.Show();
    }

    private void FillProfiles()
    {
        ProfileBox.ItemsSource = _settings.Profiles.ToList();
        ProfileBox.SelectedItem = _settings.CurrentProfile;
    }

    private void OnProfileChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || ProfileBox.SelectedItem is not string profile || profile == _settings.CurrentProfile) return;

        // Laufende Spiele behalten ihren Benutzer – wechseln ist jederzeit möglich
        SwitchProfile(profile);
    }

    private void SwitchProfile(string profile)
    {
        _settings.CurrentProfile = profile;
        AppSettings.ActiveProfile = profile;
        _settings.Save();

        foreach (var g in _games) g.SavePath = SaveLocator.Find(g);
        RefreshGamesList();
        RebuildDex();
        BuildTradePanel();
        BuildBankPanel();
        SetStatus($"Benutzer gewechselt: {profile}");
    }

    private async void OnAddProfileClick(object? sender, RoutedEventArgs e)
    {
        var name = (await PromptAsync("Neuer Benutzer", "Name des Benutzers:"))?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (_settings.Profiles.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            await ShowMessageAsync("Neuer Benutzer", $"Einen Benutzer „{name}“ gibt es schon.");
            return;
        }

        _settings.Profiles.Add(name);
        _settings.Save();
        FillProfiles();
        _ready = false;
        ProfileBox.SelectedItem = name;
        _ready = true;
        SwitchProfile(name);
    }

    /// <summary>Einfaches Eingabefenster.</summary>
    private async Task<string?> PromptAsync(string title, string label)
    {
        var box = new TextBox { MinWidth = 300 };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80 };
        var cancel = new Button { Content = "Abbrechen", IsCancel = true, MinWidth = 80 };
        string? result = null;

        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = label },
                    box,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancel, ok },
                    },
                },
            },
        };
        ok.Click += (_, _) => { result = box.Text; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Opened += (_, _) => box.Focus();
        await dialog.ShowDialog(this);
        return result;
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

        // Laufende Spiele übernehmen die Änderung über settings.json
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
