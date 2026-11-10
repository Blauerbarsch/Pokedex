// Lädt die Emulator-Cores (libretro) für das eigene Betriebssystem herunter
// und legt sie nach src/DexHub.App/cores. Von dort kopiert der Build sie neben das Programm.
//
// Aufruf im Projektordner (benötigt .NET 10):
//     dotnet run scripts/FetchCores.cs            (für dieses Betriebssystem)
//     dotnet run scripts/FetchCores.cs android    (für die Android-App, 64-Bit-ARM)
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

// 3D-Konsolen (brauchen OpenGL). Nicht für jedes System verfügbar – fehlen sie, ist das kein Fehler.
string[] optional =
[
    "mupen64plus_next_libretro", // Nintendo 64 (Pokémon Stadium, Snap, Puzzle League)
    "dolphin_libretro",          // GameCube/Wii (Colosseum, XD, Box, Battle Revolution)
    "citra_libretro",            // Nintendo 3DS (X/Y bis Ultrasonne) – ROMs müssen entschlüsselt sein
];

bool android = args.Contains("android");
string platformPath, extension;
if (android)
{
    platformPath = "android";
    extension = "_android.so";
}
else if (OperatingSystem.IsWindows())
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

var target = android
    ? Path.Combine(Directory.GetCurrentDirectory(), "src", "DexHub.Android", "cores", "arm64-v8a")
    : Path.Combine(Directory.GetCurrentDirectory(), "src", "DexHub.App", "cores");
if (!Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "src", "DexHub.App")))
{
    Console.Error.WriteLine("Bitte im Hauptordner des Projekts ausführen (dort, wo DexHub.sln liegt).");
    return 1;
}
Directory.CreateDirectory(target);

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("DexHub-FetchCores/0.1");

int failed = 0;
// Für Android zusätzlich das klassische melonDS als Ersatz, falls melonDS DS dort fehlt
if (android) cores = [.. cores, "melonds_libretro"];
HashSet<string> optionalSet = android ? [] : optional.ToHashSet();
if (!android) cores = [.. cores, .. optional];

foreach (var core in cores)
{
    var fileName = core + extension;
    var url = android
        ? $"https://buildbot.libretro.com/nightly/android/latest/arm64-v8a/{fileName}.zip"
        : $"https://buildbot.libretro.com/nightly/{platformPath}/latest/{fileName}.zip";
    // Android verlangt "lib…so" als Dateiname
    var targetName = android ? "lib" + core + ".so" : fileName;
    Console.Write($"{fileName,-28} ");

    try
    {
        var zipBytes = await http.GetByteArrayAsync(url);
        using var zip = new ZipArchive(new MemoryStream(zipBytes));
        var entry = zip.Entries.FirstOrDefault(e => e.Name == fileName)
                    ?? throw new InvalidDataException("Datei nicht im Archiv gefunden");
        entry.ExtractToFile(Path.Combine(target, targetName), overwrite: true);
        Console.WriteLine($"ok ({zipBytes.Length / 1024} KB)");
    }
    catch (Exception ex) when (optionalSet.Contains(core))
    {
        Console.WriteLine($"übersprungen ({ex.Message})");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FEHLER: {ex.Message}");
        Console.WriteLine($"    Adresse: {url}");
    }
}

// Dolphin braucht seine Systemdateien (Schriften, Datenbanken). DexHub entpackt sie beim ersten Start.
if (!android)
{
    try
    {
        var sys = await http.GetByteArrayAsync("https://buildbot.libretro.com/assets/system/Dolphin.zip");
        Directory.CreateDirectory(Path.Combine(target, "system"));
        File.WriteAllBytes(Path.Combine(target, "system", "Dolphin.zip"), sys);
        Console.WriteLine($"Dolphin-Systemdateien         ok ({sys.Length / 1024} KB)");
    }
    catch (Exception ex)
    {
        Console.WriteLine("Dolphin-Systemdateien         übersprungen (" + ex.Message + ")");
    }
}

Console.WriteLine();
Console.WriteLine(failed == 0
    ? $"Fertig. Cores liegen in {target}"
    : $"{failed} Core(s) fehlgeschlagen. Die anderen liegen in {target}");
// Android: ein fehlender DS-Core (es gibt zwei zur Auswahl) soll den Build nicht abbrechen
return failed == 0 || (android && failed <= 1) ? 0 : 1;
