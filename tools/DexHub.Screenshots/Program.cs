using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using DexHub.App;
using Avalonia.VisualTree;
using DexHub.Core.Settings;
using DexHub.UI;

// Aufruf: dotnet run --project tools/DexHub.Screenshots -- <Zielordner>
var output = Path.GetFullPath(args.Length > 0 ? args[0] : "screenshots");
Directory.CreateDirectory(output);

// Beispiel-Bibliothek: leere Dateien mit Pokémon-Namen (werden am Dateinamen erkannt)
var roms = Path.Combine(Path.GetTempPath(), "dexhub-demo-roms");
Directory.CreateDirectory(roms);
foreach (var name in new[]
{
    "Pokemon - Rote Edition (Germany).gb", "Pokemon - Gelbe Edition (Germany).gbc", "Pokemon - Kristall-Edition (Germany).gbc",
    "Pokemon - Smaragd-Edition (Germany).gba", "Pokemon - Feuerrote Edition (Germany).gba",
    "Pokemon - Platin-Edition (Germany).nds", "Pokemon - SoulSilver-Edition (Germany).nds", "Pokemon - Schwarze Edition 2 (Germany).nds",
})
{
    var path = Path.Combine(roms, name);
    if (!File.Exists(path)) File.WriteAllBytes(path, new byte[0x400]);
}
var settings = AppSettings.Load();
settings.RomFolder = roms;
settings.Save();

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();

void Pump(double seconds)
{
    var end = DateTime.Now.AddSeconds(seconds);
    while (DateTime.Now < end)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(30);
    }
}

void Capture(Window window, string name)
{
    Pump(0.5);
    var frame = window.CaptureRenderedFrame();
    if (frame is null) { Console.WriteLine($"{name}: kein Bild"); return; }
    frame.Save(Path.Combine(output, name + ".png"));
    Console.WriteLine($"{name}.png");
}

var main = new MainWindow { Width = 1280, Height = 820 };
main.Show();
Pump(25); // Pokédex-Daten und Cover laden

TabControl Tabs(Window w) => w.GetVisualDescendants().OfType<TabControl>().First(t => t.Name == "MainTabs");

void CapturePages(Window window, string prefix, Func<string, bool> include)
{
    var tabs = Tabs(window);
    for (int i = 0; i < tabs.ItemCount; i++)
    {
        if (tabs.Items[i] is not TabItem { IsEnabled: true } item) continue;
        var title = new string((item.Header?.ToString() ?? $"seite{i}").Where(char.IsLetter).ToArray());
        if (!include(title)) continue;
        tabs.SelectedIndex = i;
        Pump(title == "Guide" ? 60 : 3);
        Capture(window, $"{prefix}{i:00}-{title}");
    }
}

CapturePages(main, "", _ => true);

// Handy-Ansicht (wie in der Android-App): Hoch- und Querformat
MainView.Host = new NullPlatform { IsMobile = true };
var phone = new Window { Width = 412, Height = 892, Content = new MainView() };
phone.Show();
Pump(15);
CapturePages(phone, "phone-", t => t is "Bibliothek" or "Pokédex" or "Statistik" or "Guide" or "Einstellungen");
phone.Width = 892;
phone.Height = 412;
Pump(3);
CapturePages(phone, "phone-quer-", t => t is "Bibliothek" or "Pokédex");

// Großbild-Modus
var big = new BigPictureWindow(() => [], (_, _) => Task.CompletedTask, settings, _ => { }) { Width = 1280, Height = 720 };
big.WindowState = WindowState.Normal;
big.Show();
Pump(3);
Capture(big, "99-Grossbild");

Console.WriteLine("Fertig: " + output);
