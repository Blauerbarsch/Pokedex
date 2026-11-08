using Silk.NET.SDL;

namespace DexHub.App.Input;

/// <summary>Gemeinsamer Zugriff auf SDL (Ton und Controller nutzen dieselbe Bibliothek).</summary>
internal static class SdlApi
{
    /// <summary>SDL ist nicht für gleichzeitige Aufrufe aus mehreren Threads gedacht.</summary>
    public static readonly object Lock = new();

    private static Sdl? _api;

    public static Sdl Get()
    {
        lock (Lock)
        {
            if (_api is null)
            {
                _api = Sdl.GetApi();
                // Controller auch dann lesen, wenn gerade ein anderes Fenster den Fokus hat
                _api.SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
            }
            return _api;
        }
    }
}
