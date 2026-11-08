// Lädt die Emulator-Cores (libretro) für das eigene Betriebssystem herunter
// und legt sie nach src/DexHub.App/cores. Von dort kopiert der Build sie neben das Programm.
//
// Aufruf im Projektordner (benötigt .NET 10):
//     dotnet run scripts/FetchCores.cs
//
// Die Cores stammen vom offiziellen libretro-Buildserver: https://buildbot.libretro.com
// Sie sind Open Source (meist GPL); die Lizenzen stehen in den jeweiligen Projekten.

using System.IO.Compression;
using System.Runtime.InteropServices;

string[] cores =
[
    "gambatte_libretro",  // Game Boy / Game Boy Color
    "mgba_libretro",      // Game Boy Advance
    "melondsds_libretro", // Nintendo DS
    "pokemini_libretro",  // Pokémon Mini
];

string platformPath, extension;
if (OperatingSystem.IsWindows())
{
    platformPath = "windows/x86_64";
    extension = ".dll";
}
else if (OperatingSystem.IsMacOS())
{
    platformPath = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "apple/osx/arm64" : "apple/osx/x86_64";
    extension = ".dylib";
}
else
{
    platformPath = "linux/x86_64";
    extension = ".so";
}

var target = Path.Combine(Directory.GetCurrentDirectory(), "src", "DexHub.App", "cores");
if (!Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "src", "DexHub.App")))
{
    Console.Error.WriteLine("Bitte im Hauptordner des Projekts ausführen (dort, wo DexHub.sln liegt).");
    return 1;
}
Directory.CreateDirectory(target);

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("DexHub-FetchCores/0.1");

int failed = 0;
foreach (var core in cores)
{
    var fileName = core + extension;
    var url = $"https://buildbot.libretro.com/nightly/{platformPath}/latest/{fileName}.zip";
    Console.Write($"{fileName,-28} ");

    try
    {
        var zipBytes = await http.GetByteArrayAsync(url);
        using var zip = new ZipArchive(new MemoryStream(zipBytes));
        var entry = zip.Entries.FirstOrDefault(e => e.Name == fileName)
                    ?? throw new InvalidDataException("Datei nicht im Archiv gefunden");
        entry.ExtractToFile(Path.Combine(target, fileName), overwrite: true);
        Console.WriteLine($"ok ({zipBytes.Length / 1024} KB)");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FEHLER: {ex.Message}");
        Console.WriteLine($"    Adresse: {url}");
    }
}

Console.WriteLine();
Console.WriteLine(failed == 0
    ? $"Fertig. Cores liegen in {target}"
    : $"{failed} Core(s) fehlgeschlagen. Die anderen liegen in {target}");
return failed == 0 ? 0 : 1;
