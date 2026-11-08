using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DexHub.Core.Detection;
using DexHub.Core.Emulation;
using DexHub.Core.Models;
using DexHub.Core.Pokedex;
using DexHub.Core.Saves;
using DexHub.Core.Settings;

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
        _ready = true;

        Opened += async (_, _) =>
        {
            await ScanLibraryAsync();
            await LoadPokedexAsync(forceRefresh: false);
        };
    }

    // =====================================================================
    // Bibliothek
    // =====================================================================

    private async Task ScanLibraryAsync()
    {
        if (string.IsNullOrWhiteSpace(_settings.RomFolder))
        {
            SetStatus("Bitte zuerst einen ROM-Ordner wählen.");
            return;
        }

        SetStatus("Durchsuche ROM-Ordner…");
        var settings = _settings;
        _games = await Task.Run(() =>
        {
            var games = GameDetector.ScanFolder(settings.RomFolder);
            foreach (var g in games)
                g.SavePath = SaveLocator.Find(g, settings);
            return games;
        });

        RefreshGamesList();
        RebuildDex();
        SetStatus($"{_games.Count} Spiel(e) gefunden.");
    }

    private void RefreshGamesList()
    {
        GamesList.ItemsSource = null;
        GamesList.ItemsSource = _games;
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

        try
        {
            var process = new EmulatorLauncher(_settings).Launch(game);
            SetStatus($"{game.Title} läuft…");

            // Nach dem Beenden den Spielstand neu einlesen, damit der Pokédex aktuell ist
            process.Exited += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                game.SavePath = SaveLocator.Find(game, _settings);
                RefreshGamesList();
                RebuildDex();
                SetStatus($"{game.Title} beendet – Pokédex aktualisiert.");
            });
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Spiel konnte nicht gestartet werden", ex.Message);
        }
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
        RetroArchBox.Text = _settings.RetroArchPath;
        CoresBox.Text = _settings.CoresFolder;
        SavesBox.Text = _settings.SavesFolder;
        UpdateCoreStatus();
    }

    private void ReadSettingsForm()
    {
        _settings.RomFolder = RomFolderBox.Text?.Trim() ?? "";
        _settings.RetroArchPath = RetroArchBox.Text?.Trim() ?? "";
        _settings.CoresFolder = CoresBox.Text?.Trim() ?? "";
        _settings.SavesFolder = SavesBox.Text?.Trim() ?? "";
    }

    private void UpdateCoreStatus()
    {
        var launcher = new EmulatorLauncher(_settings);
        var platforms = Enum.GetValues<Platform>().Where(p => p != Platform.Unknown).ToList();
        var found = platforms.Count(p => launcher.FindCore(p) is not null);
        CoreStatusText.Text = $"Cores gefunden für {found} von {platforms.Count} Konsolen.";
    }

    private async void OnSaveSettingsClick(object? sender, RoutedEventArgs e)
    {
        ReadSettingsForm();
        _settings.Save();
        UpdateCoreStatus();
        await ScanLibraryAsync();
    }

    private async void OnBrowseRomFolderClick(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("ROM-Ordner wählen") is { } path)
        {
            RomFolderBox.Text = path;
            ReadSettingsForm();
            _settings.Save();
            await ScanLibraryAsync();
        }
    }

    private async void OnBrowseCoresClick(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("Cores-Ordner wählen") is { } path) CoresBox.Text = path;
    }

    private async void OnBrowseSavesClick(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("Spielstand-Ordner wählen") is { } path) SavesBox.Text = path;
    }

    private async void OnBrowseRetroArchClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "RetroArch auswählen",
            AllowMultiple = false,
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) RetroArchBox.Text = path;
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
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
