using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DexHub.Core.Models;
using DexHub.Core.Settings;

namespace DexHub.Mobile;

/// <summary>Oberste Ebene der App: die gemeinsame Oberfläche, darüber das laufende Spiel.</summary>
public sealed class AndroidShell : Panel
{
    private Control? _main;
    private PlayView? _play;

    public event Action<GameEntry, string>? GameExited;

    public GameEntry? RunningGame { get; private set; }
    public string? RunningProfile { get; private set; }

    // Farbiger Streifen hinter der Statusleiste (Uhr, Akku) – passt zur roten Kopfleiste
    private readonly Border _statusBar = new()
    {
        Background = new SolidColorBrush(Color.Parse("#DC0A2D")),
        VerticalAlignment = VerticalAlignment.Top,
        IsHitTestVisible = false,
    };
    private Thickness _safe;

    public AndroidShell()
    {
        Background = new SolidColorBrush(Color.Parse("#15171C"));
        Children.Add(_statusBar);
        AttachedToVisualTree += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.InsetsManager is not { } insets) return;
            insets.SafeAreaChanged += (_, _) => ApplySafeArea(insets.SafeAreaPadding);
            ApplySafeArea(insets.SafeAreaPadding);
        };
    }

    /// <summary>Android zeichnet die App bis unter Status- und Navigationsleiste – Inhalt daraus fernhalten.</summary>
    private void ApplySafeArea(Thickness safe)
    {
        _safe = safe;
        _statusBar.Height = safe.Top;
        if (_main is not null) _main.Margin = safe;
        if (_play is not null) _play.Margin = safe;
    }

    public void ShowMain(Control main)
    {
        _main = main;
        main.Margin = _safe;
        Children.Add(main);
    }

    public string? StartGame(GameEntry game, string corePath, string profile, bool resume, Dictionary<string, string>? options)
    {
        if (_play is not null) return "Es läuft schon ein Spiel.";
        var settings = AppSettings.Load();
        AppSettings.ActiveProfile = profile;

        _play = new PlayView(game, corePath, resume, settings, options);
        RunningGame = game;
        RunningProfile = profile;
        _play.Closed += () =>
        {
            Children.Remove(_play);
            _play = null;
            RunningGame = null;
            RunningProfile = null;
            if (_main is not null) _main.IsVisible = true;
            MainActivity.Current?.SetImmersive(false);
            GameExited?.Invoke(game, profile);
        };
        if (_main is not null) _main.IsVisible = false;
        _play.Margin = _safe;
        Children.Add(_play);
        MainActivity.Current?.SetImmersive(true);
        return null;
    }

    public void SetBackground(bool background) => _play?.SetBackground(background);

    public bool HandleBack()
    {
        if (_play is null) return false;
        _play.RequestClose();
        return true;
    }
}
