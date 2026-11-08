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

    private void RefreshGamesList()
    {
        var selected = GamesList.SelectedItem;
        GamesList.ItemsSource = null;
        GamesList.ItemsSource = _games;
        GamesList.SelectedItem = selected;
    }

    private async void OnRescanClick(object? sender, RoutedEventArgs e) => await ScanLibraryAsync();

    private async void OnPlayClick(object? sender, RoutedEventArgs e) => await LaunchSelectedAsync();

    private async void OnGameDoubleTapped(object? sender, TappedEventArgs e) => await LaunchSelectedAsync();

    private async Task LaunchSelectedAsync()
    {
        if (GamesList.SelectedItem is not GameEntry game)
        {
            SetStatus("Bitte zuerst ein Spiel auswählen.");
            return;
        }

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

        // Speichert das Spiel, sofort den Pokédex aktualisieren
        session.SaveWritten += _ => Dispatcher.UIThread.Post(() =>
        {
            game.SavePath = SaveLocator.Find(game);
            RebuildDex();
            SetStatus($"Spielstand von {game.Title} gesichert – Pokédex aktualisiert.");
        });

        var window = new GameWindow(session, _settings);
        window.Closed += (_, _) =>
        {
            _runningGame = null;
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

    private void RebuildDex()
    {
        _dex = PokedexService.Build(_species, _games);
        ApplyDexFilter();
    }

    private void ApplyDexFilter()
    {
        IEnumerable<DexEntry> items = _dex;

        var query = SearchBox.Text?.Trim();
        if (!string.IsNullOrEmpty(query))
        {
            var number = int.TryParse(query.TrimStart('#'), out var n) ? n : -1;
            items = items.Where(d => d.Id == number
                || d.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || d.Species.NameEn.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        if (GenFilter.SelectedIndex > 0)
            items = items.Where(d => d.Species.Generation == GenFilter.SelectedIndex);

        if (OnlyCaughtBox.IsChecked == true)
            items = items.Where(d => d.IsCaught);

        var list = items.ToList();
        DexList.ItemsSource = list;
        DexCounter.Text = $"{list.Count(d => d.IsCaught)} / {list.Count} gefangen";
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) { if (_ready) ApplyDexFilter(); }
    private void OnGenFilterChanged(object? sender, SelectionChangedEventArgs e) { if (_ready) ApplyDexFilter(); }
    private void OnOnlyCaughtChanged(object? sender, RoutedEventArgs e) { if (_ready) ApplyDexFilter(); }

    private async void OnReloadDexClick(object? sender, RoutedEventArgs e) => await LoadPokedexAsync(forceRefresh: true);

    private async void OnDexSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || DexList.SelectedItem is not DexEntry entry) return;

        DetailName.Text = $"{entry.Number} {entry.Name}";
        DetailText.Text = entry.DetailText;
        SpriteImage.Source = null;

        // Bild nachladen; ältere Ladevorgänge abbrechen, wenn man schnell durch die Liste scrollt
        _spriteCts?.Cancel();
        _spriteCts = new CancellationTokenSource();
        var token = _spriteCts.Token;

        try
        {
            var path = await _pokeApi.GetSpritePathAsync(entry.Id, token);
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
        SmoothBox.IsChecked = _settings.SmoothScaling;
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

    private void OnSmoothChanged(object? sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _settings.SmoothScaling = SmoothBox.IsChecked == true;
        SaveAndApply();
    }

    /// <summary>Speichert und gibt Änderungen sofort an ein laufendes Spiel weiter.</summary>
    private void SaveAndApply()
    {
        _settings.Save();
        _runningGame?.ApplySettings();
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
            Text = "Auf eine Belegung klicken und dann die gewünschte Taste drücken. " +
                   "Esc bricht ab, Entf löscht eine Tastatur-Belegung. Änderungen gelten sofort, auch im laufenden Spiel.",
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

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("170,200,250") };
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

            var padButton = new Button { Content = padText, HorizontalAlignment = HorizontalAlignment.Stretch };
            padButton.Click += (_, _) => StartPadCapture(padButton, setPad);
            AddCell(padButton, 2);
            row++;
        }

        // Kopfzeile
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        AddCell(new TextBlock { Text = "Spieltaste", FontWeight = FontWeight.SemiBold }, 0);
        AddCell(new TextBlock { Text = "Tastatur", FontWeight = FontWeight.SemiBold }, 1);
        AddCell(new TextBlock { Text = "Controller", FontWeight = FontWeight.SemiBold }, 2);
        row++;

        // Neue Wörterbücher statt Änderungen an den alten: der Emulator-Thread liest gleichzeitig mit
        foreach (var (button, label) in InputSettings.ButtonLabels)
        {
            AddRow(label,
                InputSettings.KeyLabel(input.Keyboard.GetValueOrDefault(button, "")),
                key => input.Keyboard = new(input.Keyboard) { [button] = key },
                InputSettings.PadLabel(input.Gamepad.GetValueOrDefault(button, PadControl.None)),
                pad => input.Gamepad = new(input.Gamepad) { [button] = pad });
        }

        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            AddRow(InputSettings.HotkeyLabel(action),
                InputSettings.KeyLabel(input.HotkeyKeyboard.GetValueOrDefault(action, "")),
                key => input.HotkeyKeyboard = new(input.HotkeyKeyboard) { [action] = key },
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
            Text = "Feste Tasten im Spiel: P = Pause · F5 = Schnellspeichern · F9 = Schnellladen · F11 = Vollbild",
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        });
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
