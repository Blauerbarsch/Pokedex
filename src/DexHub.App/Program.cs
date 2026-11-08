using Avalonia;

namespace DexHub.App;

/// <summary>
/// Startparameter für ein einzelnes Spiel. Jedes Spiel läuft als eigener Prozess, weil ein
/// Emulator-Core pro Programm nur einmal geladen sein kann – so laufen mehrere Spiele gleichzeitig.
/// Aufruf: DexHub --play "rom.nds" --profile "Name" [--fresh] [--option key=value …]
/// </summary>
public sealed record PlayRequest(string RomPath, string Profile, bool Resume, Dictionary<string, string> Options)
{
    public static PlayRequest? Parse(string[] args)
    {
        int i = Array.IndexOf(args, "--play");
        if (i < 0 || i + 1 >= args.Length) return null;

        string rom = args[i + 1], profile = Core.Settings.AppSettings.DefaultProfile;
        bool resume = true;
        var options = new Dictionary<string, string>();
        for (int a = 0; a < args.Length; a++)
        {
            if (args[a] == "--profile" && a + 1 < args.Length) profile = args[++a];
            else if (args[a] == "--fresh") resume = false;
            else if (args[a] == "--option" && a + 1 < args.Length)
            {
                var pair = args[++a];
                int eq = pair.IndexOf('=');
                if (eq > 0) options[pair[..eq]] = pair[(eq + 1)..];
            }
        }
        return new PlayRequest(rom, profile, resume, options);
    }
}

internal static class Program
{
    /// <summary>Gesetzt, wenn dieser Prozess nur ein Spiel zeigen soll.</summary>
    public static PlayRequest? Play { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        Play = PlayRequest.Parse(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Wird auch vom Avalonia-Designer in der IDE benutzt
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
