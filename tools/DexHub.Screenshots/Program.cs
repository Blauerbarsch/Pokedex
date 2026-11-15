using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using DexHub.App;
using DexHub.Core.Settings;

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

var tabs = main.FindControl<TabControl>("MainTabs")!;
for (int i = 0; i < tabs.ItemCount; i++)
{
    if (tabs.Items[i] is not TabItem { IsEnabled: true } item) continue;
    tabs.SelectedIndex = i;
    var title = (item.Header?.ToString() ?? $"seite{i}").Trim();
    title = new string(title.Where(char.IsLetter).ToArray());
    Pump(title == "Guide" ? 60 : 3);
    Capture(main, $"{i:00}-{title}");
}

// Großbild-Modus
var big = new BigPictureWindow(() => [], (_, _) => Task.CompletedTask, settings, _ => { }) { Width = 1280, Height = 720 };
big.WindowState = WindowState.Normal;
big.Show();
Pump(3);
Capture(big, "99-Grossbild");

Console.WriteLine("Fertig: " + output);
