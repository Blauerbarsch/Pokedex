using Avalonia.Controls;
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

    public void ShowMain(Control main)
    {
        _main = main;
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
