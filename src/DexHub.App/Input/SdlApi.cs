using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;
using Silk.NET.SDL;

namespace DexHub.App.Input;

/// <summary>Gemeinsamer Zugriff auf SDL (Ton und Controller nutzen dieselbe Bibliothek).</summary>
internal static class SdlApi
{
    /// <summary>SDL ist nicht für gleichzeitige Aufrufe aus mehreren Threads gedacht.</summary>
    public static readonly object Lock = new();

    private static Sdl? _api;
    private static Exception? _loadError;

    /// <summary>
    /// Dateinamen, unter denen SDL2 auf den Systemen liegt. Unter Linux gibt es meist nur
    /// "libSDL2-2.0.so.0" (ohne Entwicklerpaket fehlt "libSDL2.so").
    /// </summary>
    private static string[] Candidates()
    {
        var own = AppContext.BaseDirectory;
        if (OperatingSystem.IsWindows())
            return ["SDL2.dll", Path.Combine(own, "SDL2.dll")];
        if (OperatingSystem.IsMacOS())
            return
            [
                Path.Combine(own, "libSDL2-2.0.0.dylib"),
                "libSDL2-2.0.0.dylib", "libSDL2.dylib",
                "/opt/homebrew/lib/libSDL2-2.0.0.dylib", "/usr/local/lib/libSDL2-2.0.0.dylib",
            ];
        return
        [
            Path.Combine(own, "libSDL2-2.0.so.0"),
            "libSDL2-2.0.so.0", "libSDL2-2.0.so", "libSDL2.so",
            "/usr/lib64/libSDL2-2.0.so.0", "/usr/lib/x86_64-linux-gnu/libSDL2-2.0.so.0", "/usr/lib/libSDL2-2.0.so.0",
        ];
    }

    public static Sdl Get()
    {
        lock (Lock)
        {
            if (_api is not null) return _api;
            if (_loadError is not null) throw _loadError;

            _api = TryLoad();
            if (_api is null)
            {
                _loadError = new DllNotFoundException(
                    "SDL2 wurde nicht gefunden. Gesucht wurde: " + string.Join(", ", Candidates()));
                Console.WriteLine("[DexHub] " + _loadError.Message);
                throw _loadError;
            }

            // Controller auch dann lesen, wenn gerade ein anderes Fenster den Fokus hat
            _api.SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
            return _api;
        }
    }

    private static Sdl? TryLoad()
    {
        // 1. Mitgelieferte Version aus dem NuGet-Paket
        try
        {
            var api = Sdl.GetApi();
            Console.WriteLine("[DexHub] SDL2 aus dem Paket geladen");
            return api;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[DexHub] SDL2 aus dem Paket nicht verfügbar: " + ex.Message);
        }

        // 2. SDL2 des Betriebssystems
        foreach (var candidate in Candidates())
        {
            if (!NativeLibrary.TryLoad(candidate, out var handle)) continue;
            NativeLibrary.Free(handle);
            try
            {
                var api = new Sdl(new DefaultNativeContext(candidate));
                Console.WriteLine("[DexHub] SDL2 geladen: " + candidate);
                return api;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DexHub] {candidate} gefunden, aber nicht nutzbar: {ex.Message}");
            }
        }
        return null;
    }
}
