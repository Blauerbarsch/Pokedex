using DexHub.Core.Models;
using DexHub.Core.Settings;

namespace DexHub.UI;

/// <summary>
/// Was die gemeinsame Oberfläche von der jeweiligen App braucht (Desktop: Spiele als eigene Prozesse,
/// SDL-Controller, Großbild-Fenster; Android: Spiel im selben Fenster, Android-Controller).
/// </summary>
public interface IPlatform
{
    /// <summary>Handy/Tablet: ROMs werden importiert statt aus einem Ordner gelesen, keine Updates im Programm.</summary>
    bool IsMobile { get; }

    /// <summary>Startet ein Spiel. Rückgabe: Fehlermeldung oder null.</summary>
    Task<string?> LaunchAsync(GameEntry game, string profile, bool resume, Dictionary<string, string>? options, bool fullscreen);

    bool IsRunning(GameEntry game, string profile);

    /// <summary>Läuft irgendein Spiel (optional: von diesem Benutzer)?</summary>
    bool AnyRunning(string? profile = null);

    /// <summary>Ein Spiel wurde beendet (Spielstände neu einlesen).</summary>
    event Action<GameEntry, string>? GameExited;

    IGamepadInfo Gamepad { get; }

    /// <summary>Ordner im Dateimanager öffnen (Android: nicht möglich -> false).</summary>
    bool OpenFolder(string path);

    /// <summary>Großbild-Modus (nur Desktop).</summary>
    bool SupportsBigPicture { get; }
    void OpenBigPicture(Func<List<GameEntry>> games, Func<GameEntry, bool, Task> launch, AppSettings settings, Action<string> switchProfile);

    /// <summary>Direkt im Großbild-Modus starten (Desktop: --bigpicture).</summary>
    bool StartInBigPicture { get; }

    /// <summary>Fester ROM-Ordner der App (Android); null = Nutzer wählt einen Ordner.</summary>
    string? DefaultRomFolder => null;

    /// <summary>Für automatische Tests: dieses Spiel (Dateiname) nach dem Start sofort spielen.</summary>
    string? AutostartRom => null;

    /// <summary>Für automatische Tests: diese Seite (Reiter-Überschrift) nach dem Start zeigen.</summary>
    string? StartTab => null;
}

/// <summary>Controller-Infos für die Seite "Steuerung".</summary>
public interface IGamepadInfo
{
    IReadOnlyList<string> ConnectedNames();
    string? InitError { get; }
    IReadOnlyList<string> UnmappedDevices { get; }
    PadControl FirstPressed();
}

/// <summary>Platzhalter, solange keine Plattform gesetzt ist (z. B. Bildschirmfotos).</summary>
public sealed class NullPlatform : IPlatform, IGamepadInfo
{
    public bool IsMobile { get; init; }
    public Task<string?> LaunchAsync(GameEntry game, string profile, bool resume, Dictionary<string, string>? options, bool fullscreen) =>
        Task.FromResult<string?>("Spielen ist in dieser Ansicht nicht möglich.");
    public bool IsRunning(GameEntry game, string profile) => false;
    public bool AnyRunning(string? profile = null) => false;
    public event Action<GameEntry, string>? GameExited { add { } remove { } }
    public IGamepadInfo Gamepad => this;
    public bool OpenFolder(string path) => false;
    public bool SupportsBigPicture => false;
    public void OpenBigPicture(Func<List<GameEntry>> games, Func<GameEntry, bool, Task> launch, AppSettings settings, Action<string> switchProfile) { }
    public bool StartInBigPicture => false;
    public IReadOnlyList<string> ConnectedNames() => [];
    public string? InitError => null;
    public IReadOnlyList<string> UnmappedDevices => [];
    public PadControl FirstPressed() => PadControl.None;
}
