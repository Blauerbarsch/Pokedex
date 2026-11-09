using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DexHub.App.Input;
using DexHub.Core.Covers;
using DexHub.Core.Models;
using DexHub.Core.Settings;

namespace DexHub.App;

/// <summary>
/// Großbild-Modus: Vollbild, komplett mit Controller (oder Pfeiltasten/Enter) bedienbar.
/// Gedacht für Fernseher, Steam Deck und Bazzite.
/// </summary>
public sealed class BigPictureWindow : Window
{
    private readonly Func<List<GameEntry>> _games;
    private readonly Func<GameEntry, bool, Task> _launch;
    private readonly AppSettings _settings;
    private readonly Action<string> _switchProfile;
    private readonly CoverService _covers = new();

    private readonly WrapPanel _grid = new();
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _profileText = new() { FontSize = 26, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White };
    private readonly TextBlock _selectedText = new() { FontSize = 30, FontWeight = FontWeight.Bold, Foreground = Brushes.White };
    private readonly TextBlock _detailText = new() { FontSize = 18, Foreground = Brushes.LightGray };
    private readonly List<(GameEntry Game, Border Tile)> _tiles = [];
    private int _index;

    private readonly DispatcherTimer _padTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Dictionary<PadControl, (bool Down, DateTime Next)> _padState = [];

    private const double TileWidth = 220, TileHeight = 300;
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#E3350D"));

    public BigPictureWindow(Func<List<GameEntry>> games, Func<GameEntry, bool, Task> launch,
        AppSettings settings, Action<string> switchProfile)
    {
        _games = games;
        _launch = launch;
        _settings = settings;
        _switchProfile = switchProfile;

        Title = "DexHub – Großbild";
        Background = new SolidColorBrush(Color.Parse("#101418"));
        WindowState = WindowState.FullScreen;

        _scroll = new ScrollViewer { Content = _grid, Margin = new Thickness(40, 0) };

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(40, 30, 40, 10),
            Children =
            {
                new TextBlock { Text = "DexHub", FontSize = 40, FontWeight = FontWeight.Bold, Foreground = Accent },
                WithColumn(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 16,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = { Hint("LB"), _profileText, Hint("RB") },
                }, 1),
            },
        };

        var info = new StackPanel { Margin = new Thickness(40, 10, 40, 0), Spacing = 4, Children = { _selectedText, _detailText } };

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 30,
            Margin = new Thickness(40, 10, 40, 30),
            Children =
            {
                Legend("A", "Spielen"), Legend("Y", "Ohne Fortsetzen"), Legend("LB / RB", "Benutzer"),
                Legend("B", "Großbild beenden"),
            },
        };

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(info, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(info);
        root.Children.Add(footer);
        root.Children.Add(_scroll);
        Content = root;

        KeyDown += OnKeyDown;
        Opened += (_, _) =>
        {
            Rebuild();
            _padTimer.Start();
        };
        Closed += (_, _) => _padTimer.Stop();
        _padTimer.Tick += (_, _) => PollGamepad();
    }

    private static Control WithColumn(Control c, int column)
    {
        Grid.SetColumn(c, column);
        return c;
    }

    private static Border Hint(string text) => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(10, 4),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 18, FontWeight = FontWeight.Bold, Foreground = Brushes.White },
    };

    private static Control Legend(string button, string action) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 10,
        Children = { Hint(button), new TextBlock { Text = action, FontSize = 18, Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center } },
    };

    // =====================================================================
    // Kacheln
    // =====================================================================

    private void Rebuild()
    {
        _profileText.Text = "👤 " + _settings.CurrentProfile;
        _grid.Children.Clear();
        _tiles.Clear();

        foreach (var game in _games().OrderBy(g => g.Info.Platform).ThenBy(g => g.Info.Generation ?? 99).ThenBy(g => g.Title))
        {
            var cover = new Image { Width = TileWidth - 30, Height = TileWidth - 30, Stretch = Stretch.Uniform };
            var tile = new Border
            {
                Width = TileWidth,
                Height = TileHeight,
                Margin = new Thickness(10),
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                BorderThickness = new Thickness(4),
                BorderBrush = Brushes.Transparent,
                Child = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        cover,
                        new TextBlock
                        {
                            Text = game.Title,
                            FontSize = 17,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = Brushes.White,
                            TextWrapping = TextWrapping.Wrap,
                            TextAlignment = TextAlignment.Center,
                        },
                    },
                },
            };
            var g = game;
            tile.PointerPressed += async (_, e) =>
            {
                Select(_tiles.FindIndex(t => t.Game == g));
                if (e.ClickCount == 2) await LaunchSelected(resume: true);
            };
            _tiles.Add((game, tile));
            _grid.Children.Add(tile);
            _ = LoadCover(game, cover);
        }

        Select(Math.Clamp(_index, 0, Math.Max(0, _tiles.Count - 1)));
    }

    private async Task LoadCover(GameEntry game, Image target)
    {
        try
        {
            if (await _covers.GetCoverPathAsync(game) is { } path) target.Source = new Bitmap(path);
        }
        catch { /* Cover sind nur Deko */ }
    }

    private void Select(int index)
    {
        if (_tiles.Count == 0)
        {
            _selectedText.Text = "Keine Spiele gefunden – ROM-Ordner im normalen Fenster wählen.";
            _detailText.Text = "";
            return;
        }

        _index = Math.Clamp(index, 0, _tiles.Count - 1);
        for (int i = 0; i < _tiles.Count; i++)
        {
            var tile = _tiles[i].Tile;
            bool selected = i == _index;
            tile.BorderBrush = selected ? Accent : Brushes.Transparent;
            tile.RenderTransform = selected ? new ScaleTransform(1.05, 1.05) : null;
        }

        var game = _tiles[_index].Game;
        _selectedText.Text = game.Title;
        _detailText.Text = $"{game.PlatformName}  ·  {game.StatusText}";
        _tiles[_index].Tile.BringIntoView();
    }

    private int Columns => Math.Max(1, (int)((_scroll.Bounds.Width) / (TileWidth + 20)));

    private void Move(int dx, int dy) => Select(_index + dx + dy * Columns);

    private async Task LaunchSelected(bool resume)
    {
        if (_tiles.Count == 0) return;
        await _launch(_tiles[_index].Game, resume);
    }

    private void SwitchProfile(int direction)
    {
        var profiles = _settings.Profiles;
        if (profiles.Count < 2) return;
        int i = profiles.IndexOf(_settings.CurrentProfile);
        var next = profiles[((i + direction) % profiles.Count + profiles.Count) % profiles.Count];
        _switchProfile(next);
        Rebuild();
    }

    // =====================================================================
    // Eingabe
    // =====================================================================

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left: Move(-1, 0); break;
            case Key.Right: Move(1, 0); break;
            case Key.Up: Move(0, -1); break;
            case Key.Down: Move(0, 1); break;
            case Key.Enter: await LaunchSelected(resume: true); break;
            case Key.PageUp: SwitchProfile(-1); break;
            case Key.PageDown: SwitchProfile(1); break;
            case Key.Escape: Close(); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Controller abfragen – nur wenn dieses Fenster vorne ist (sonst steuert man das laufende Spiel).</summary>
    private void PollGamepad()
    {
        if (!IsActive) return;
        var pad = SdlGamepad.Instance;

        // Richtungen wiederholen sich beim Gedrückthalten (erst nach 400 ms, dann alle 120 ms)
        if (Pressed(PadControl.DpadLeft, true) || Pressed(PadControl.LeftStickLeft, true)) Move(-1, 0);
        if (Pressed(PadControl.DpadRight, true) || Pressed(PadControl.LeftStickRight, true)) Move(1, 0);
        if (Pressed(PadControl.DpadUp, true) || Pressed(PadControl.LeftStickUp, true)) Move(0, -1);
        if (Pressed(PadControl.DpadDown, true) || Pressed(PadControl.LeftStickDown, true)) Move(0, 1);

        // Menü-Belegung wie bei Xbox/Steam: untere Taste bestätigt, rechte Taste geht zurück
        if (Pressed(PadControl.South)) _ = LaunchSelected(resume: true);
        if (Pressed(PadControl.North)) _ = LaunchSelected(resume: false);
        if (Pressed(PadControl.East)) Close();
        if (Pressed(PadControl.LeftShoulder)) SwitchProfile(-1);
        if (Pressed(PadControl.RightShoulder)) SwitchProfile(1);
        if (Pressed(PadControl.Back)) Close();

        bool Pressed(PadControl control, bool repeat = false)
        {
            bool down = pad.IsDown(control);
            var now = DateTime.UtcNow;
            var (wasDown, next) = _padState.GetValueOrDefault(control);
            _padState[control] = (down, next);

            if (!down) return false;
            if (!wasDown)
            {
                _padState[control] = (true, now.AddMilliseconds(400));
                return true;
            }
            if (repeat && now >= next)
            {
                _padState[control] = (true, now.AddMilliseconds(120));
                return true;
            }
            return false;
        }
    }
}
