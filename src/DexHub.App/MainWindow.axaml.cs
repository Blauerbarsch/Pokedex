using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DexHub.App.Audio;
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

        SetStatus("Durchsuche ROM-Ordner…");
        var romFolder = _settings.RomFolder;
        _games = await Task.Run(() =>
        {
            var games = GameDetector.ScanFolder(romFolder);
            foreach (var g in games)
                g.SavePath = SaveLocator.Find(g);
            return games;
        });

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

        // Speichert das Spiel, sofort den Pokédex aktualisieren
        session.SaveWritten += _ => Dispatcher.UIThread.Post(() =>
        {
            game.SavePath = SaveLocator.Find(game);
            RebuildDex();
            SetStatus($"Spielstand von {game.Title} gesichert – Pokédex aktualisiert.");
        });

        var window = new GameWindow(session);
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

        FoldersText.Text =
            $"Spielstände: {AppSettings.SavesFolder}\n" +
            $"Schnellspeicherungen: {AppSettings.StatesFolder}\n" +
            $"BIOS/Firmware (optional): {AppSettings.SystemFolder}";
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
