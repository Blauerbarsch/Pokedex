using System.Diagnostics;
using Avalonia.Threading;
using DexHub.App.Audio;
using DexHub.App.Input;
using DexHub.Core.Detection;
using DexHub.Core.Emulation;
using DexHub.Core.Settings;

namespace DexHub.App;

/// <summary>Startet Spiele als eigene Prozesse und richtet ein Spiel im Kind-Prozess ein.</summary>
public static class PlayerHost
{
    /// <summary>Startinfo für einen neuen Spiel-Prozess (dasselbe Programm mit --play).</summary>
    public static ProcessStartInfo CreateStartInfo(string romPath, string profile, bool resume,
        IReadOnlyDictionary<string, string>? options = null)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Programmpfad unbekannt.");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };

        // Läuft DexHub über "dotnet DexHub.dll", muss die DLL mit übergeben werden
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(typeof(PlayerHost).Assembly.Location);

        psi.ArgumentList.Add("--play");
        psi.ArgumentList.Add(romPath);
        psi.ArgumentList.Add("--profile");
        psi.ArgumentList.Add(profile);
        if (!resume) psi.ArgumentList.Add("--fresh");
        foreach (var (key, value) in options ?? new Dictionary<string, string>())
        {
            psi.ArgumentList.Add("--option");
            psi.ArgumentList.Add($"{key}={value}");
        }
        return psi;
    }

    /// <summary>Im Spiel-Prozess: Spiel laden und Fenster erzeugen.</summary>
    public static GameWindow CreateWindow(PlayRequest play)
    {
        var settings = AppSettings.Load();
        AppSettings.ActiveProfile = play.Profile; // Spielstände dieses Benutzers – bleibt für den ganzen Prozess

        var game = GameDetector.Detect(play.RomPath)
                   ?? throw new InvalidOperationException("Unbekannte ROM: " + play.RomPath);
        var corePath = CoreCatalog.FindCore(game.Info.Platform)
                       ?? throw new InvalidOperationException($"Kein Emulator für {game.PlatformName} gefunden.");

        var session = new EmulatorSession(game, corePath, new SdlAudioSink());
        SdlGamepad.Instance.Settings = settings.Input;
        session.InputProvider = SdlGamepad.Instance;
        session.AutoResume = play.Resume && settings.AutoResume;

        var options = new Dictionary<string, string>(settings.CoreOptions.GetValueOrDefault(session.CoreName) ?? []);
        foreach (var (k, v) in play.Options) options[k] = v; // z. B. Linkkabel
        session.CoreOptions = options;

        var window = new GameWindow(session, settings);
        // Bei mehreren Fenstern sofort sehen, wer wo spielt
        window.Title = $"{play.Profile} spielt: {game.Title} – DexHub";
        if (play.Options.Keys.Any(k => k.Contains("link", StringComparison.OrdinalIgnoreCase)))
            window.Title += " – Linkkabel";

        WatchSettings(window, play);
        return window;
    }

    /// <summary>
    /// Änderungen im Hauptfenster (Lautstärke, Tasten, Bild …) landen in settings.json.
    /// Das Spiel schaut jede Sekunde nach und übernimmt sie.
    /// </summary>
    private static void WatchSettings(GameWindow window, PlayRequest play)
    {
        var file = AppSettings.SettingsFilePath;
        var lastWrite = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            if (!File.Exists(file)) return;
            var current = File.GetLastWriteTimeUtc(file);
            if (current == lastWrite) return;
            lastWrite = current;

            var fresh = AppSettings.Load();
            AppSettings.ActiveProfile = play.Profile;
            window.ReplaceSettings(fresh);
        };
        timer.Start();
        window.Closed += (_, _) => timer.Stop();
    }
}
