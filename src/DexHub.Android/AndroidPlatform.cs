using DexHub.Core.Emulation;
using DexHub.Core.Models;
using DexHub.Core.Settings;
using DexHub.UI;

namespace DexHub.Mobile;

/// <summary>Android-Seite der gemeinsamen Oberfläche: Spiele laufen im selben Fenster (immer nur eins).</summary>
public sealed class AndroidPlatform(AndroidShell shell) : IPlatform
{
    public bool IsMobile => true;

    /// <summary>Wo die App ihre ROMs ablegt (gesetzt von MainActivity).</summary>
    public static string RomFolder { get; set; } = "";

    /// <summary>Für automatische Tests: ROM-Dateiname, der nach dem Start sofort gespielt wird.</summary>
    public static string? Autostart { get; set; }

    public string? DefaultRomFolder => RomFolder;
    public string? AutostartRom => Autostart;

    /// <summary>Für automatische Tests: Seite, die nach dem Start gezeigt wird.</summary>
    public static string? StartTabName { get; set; }
    public string? StartTab => StartTabName;

    public event Action<GameEntry, string>? GameExited
    {
        add => shell.GameExited += value;
        remove => shell.GameExited -= value;
    }

    public Task<string?> LaunchAsync(GameEntry game, string profile, bool resume, Dictionary<string, string>? options, bool fullscreen)
    {
        var core = CoreCatalog.FindCore(game.Info.Platform);
        if (core is null) return Task.FromResult<string?>($"Für {game.Info.Platform.DisplayName()} ist in der Android-App kein Emulator enthalten.");
        return Task.FromResult(shell.StartGame(game, core, profile, resume, options));
    }

    public bool IsRunning(GameEntry game, string profile) =>
        shell.RunningGame?.RomPath == game.RomPath && shell.RunningProfile == profile;

    public bool AnyRunning(string? profile = null) =>
        shell.RunningGame is not null && (profile is null || shell.RunningProfile == profile);

    public IGamepadInfo Gamepad => AndroidGamepad.Instance;

    public bool OpenFolder(string path) => false;

    public bool SupportsBigPicture => false;
    public void OpenBigPicture(Func<List<GameEntry>> games, Func<GameEntry, bool, Task> launch, AppSettings settings, Action<string> switchProfile) { }
    public bool StartInBigPicture => false;
}
