using System.Diagnostics;
using Avalonia.Threading;
using DexHub.App.Input;
using DexHub.Core.Models;
using DexHub.Core.Settings;
using DexHub.UI;

namespace DexHub.App;

/// <summary>
/// Desktop-Seite der gemeinsamen Oberfläche: Spiele laufen als eigene Prozesse (mehrere gleichzeitig,
/// je Benutzer), Controller über SDL, Großbild-Modus als eigenes Fenster.
/// </summary>
public sealed class DesktopPlatform : IPlatform
{
    public static readonly DesktopPlatform Instance = new();

    private sealed record RunningGame(GameEntry Game, string Profile, Process Process);
    private readonly List<RunningGame> _running = [];
    private BigPictureWindow? _bigPicture;

    public bool IsMobile => false;

    public event Action<GameEntry, string>? GameExited;

    public Task<string?> LaunchAsync(GameEntry game, string profile, bool resume, Dictionary<string, string>? options, bool fullscreen)
    {
        var process = Process.Start(PlayerHost.CreateStartInfo(game.RomPath, profile, resume, options, fullscreen));
        if (process is null) return Task.FromResult<string?>("Prozess konnte nicht gestartet werden.");

        var running = new RunningGame(game, profile, process);
        lock (_running) _running.Add(running);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            lock (_running) _running.Remove(running);
            Dispatcher.UIThread.Post(() => GameExited?.Invoke(game, profile));
        };
        return Task.FromResult<string?>(null);
    }

    public bool IsRunning(GameEntry game, string profile)
    {
        lock (_running) return _running.Any(r => !r.Process.HasExited && r.Game.RomPath == game.RomPath && r.Profile == profile);
    }

    public bool AnyRunning(string? profile = null)
    {
        lock (_running) return _running.Any(r => !r.Process.HasExited && (profile is null || r.Profile == profile));
    }

    public IGamepadInfo Gamepad => SdlGamepad.Instance;

    public bool OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool SupportsBigPicture => true;

    public bool StartInBigPicture => Program.StartInBigPicture;

    public void OpenBigPicture(Func<List<GameEntry>> games, Func<GameEntry, bool, Task> launch, AppSettings settings, Action<string> switchProfile)
    {
        if (_bigPicture is not null)
        {
            _bigPicture.Activate();
            return;
        }
        _bigPicture = new BigPictureWindow(games, launch, settings, switchProfile);
        _bigPicture.Closed += (_, _) => _bigPicture = null;
        _bigPicture.Show();
    }
}
