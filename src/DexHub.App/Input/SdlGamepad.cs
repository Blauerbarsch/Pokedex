using System.Diagnostics;
using System.Runtime.InteropServices;
using DexHub.Core.Emulation;
using DexHub.Core.Libretro;
using DexHub.Core.Settings;
using Silk.NET.SDL;

namespace DexHub.App.Input;

/// <summary>
/// Liest Controller über SDL2 (Xbox, PlayStation, Switch Pro, Steam Deck …).
/// Wird sowohl vom Emulator-Thread (im Spiel) als auch vom UI-Thread (Tasten belegen) benutzt.
/// </summary>
public sealed unsafe class SdlGamepad : IInputProvider
{
    public static SdlGamepad Instance { get; } = new();

    private const uint InitGameController = 0x00002000; // schließt Joystick + Events mit ein
    private const short StickThreshold = 16000;           // von 32767
    private const short TriggerThreshold = 8000;

    private readonly List<IntPtr> _controllers = [];
    private readonly Stopwatch _rescanTimer = Stopwatch.StartNew();
    private Sdl? _sdl;
    private bool _initFailed;
    private int _lastJoystickCount = -1;

    /// <summary>Geräte, die SDL sieht, aber nicht als Controller zuordnen kann (für die Diagnose).</summary>
    public List<string> UnmappedDevices { get; } = [];

    /// <summary>Warum kein Controller verfügbar ist, falls SDL nicht startet.</summary>
    public string? InitError { get; private set; }

    /// <summary>Belegung, die beim Spielen benutzt wird.</summary>
    public InputSettings? Settings { get; set; }

    private SdlGamepad() { }

    // =====================================================================
    // Öffentliche Abfragen
    // =====================================================================

    /// <summary>Namen der angeschlossenen Controller.</summary>
    public List<string> ConnectedNames()
    {
        lock (SdlApi.Lock)
        {
            if (!EnsureInit()) return [];
            Rescan(force: true);
            return _controllers
                .Select(c => Marshal.PtrToStringUTF8((IntPtr)_sdl!.GameControllerName((GameController*)c)) ?? "Controller")
                .ToList();
        }
    }

    /// <summary>Für das Spiel: gedrückte Spieltasten laut Belegung.</summary>
    public InputSnapshot Poll()
    {
        var settings = Settings;
        if (settings is null) return default;

        lock (SdlApi.Lock)
        {
            if (!EnsureInit()) return default;
            Update();

            int buttons = 0;
            foreach (var (button, control) in settings.Gamepad)
                if (IsPressed(control)) buttons |= 1 << (int)button;

            if (settings.LeftStickAsDpad)
            {
                if (IsPressed(PadControl.LeftStickUp)) buttons |= 1 << (int)JoypadButton.Up;
                if (IsPressed(PadControl.LeftStickDown)) buttons |= 1 << (int)JoypadButton.Down;
                if (IsPressed(PadControl.LeftStickLeft)) buttons |= 1 << (int)JoypadButton.Left;
                if (IsPressed(PadControl.LeftStickRight)) buttons |= 1 << (int)JoypadButton.Right;
            }

            bool fastForward = settings.HotkeyGamepad.TryGetValue(HotkeyAction.FastForward, out var ff) && IsPressed(ff);
            return new InputSnapshot(buttons, fastForward);
        }
    }

    /// <summary>Für "Taste belegen": die erste gerade gedrückte Controller-Taste, sonst None.</summary>
    public PadControl FirstPressed()
    {
        lock (SdlApi.Lock)
        {
            if (!EnsureInit()) return PadControl.None;
            Update();
            foreach (var control in Enum.GetValues<PadControl>())
                if (control != PadControl.None && IsPressed(control))
                    return control;
            return PadControl.None;
        }
    }

    // =====================================================================
    // Intern (immer unter SdlApi.Lock aufrufen)
    // =====================================================================

    private bool EnsureInit()
    {
        if (_sdl is not null) return true;
        if (_initFailed) return false;
        try
        {
            var sdl = SdlApi.Get();
            if (sdl.InitSubSystem(InitGameController) < 0)
            {
                InitError = sdl.GetErrorS();
                Console.WriteLine("[DexHub] Controller-Unterstützung nicht verfügbar: " + InitError);
                _initFailed = true;
                return false;
            }
            _sdl = sdl;
            return true;
        }
        catch (Exception ex)
        {
            InitError = ex.Message;
            Console.WriteLine("[DexHub] SDL konnte nicht geladen werden: " + ex);
            _initFailed = true;
            return false;
        }
    }

    private void Update()
    {
        // Neue/abgezogene Controller etwa jede Sekunde erkennen
        if (_rescanTimer.ElapsedMilliseconds > 1000) Rescan(force: false);
        _sdl!.GameControllerUpdate();
    }

    private void Rescan(bool force)
    {
        _rescanTimer.Restart();
        _sdl!.PumpEvents();
        int count = _sdl.NumJoysticks();
        if (!force && count == _lastJoystickCount) return;
        _lastJoystickCount = count;

        foreach (var c in _controllers) _sdl.GameControllerClose((GameController*)c);
        _controllers.Clear();

        Console.WriteLine($"[DexHub] SDL sieht {count} Eingabegerät(e)");
        UnmappedDevices.Clear();
        for (int i = 0; i < count; i++)
        {
            var deviceName = Marshal.PtrToStringUTF8((IntPtr)_sdl.JoystickNameForIndex(i)) ?? "?";
            bool isController = _sdl.IsGameController(i) == SdlBool.True;
            Console.WriteLine($"[DexHub]   #{i}: {deviceName} – {(isController ? "Controller erkannt" : "keine Tastenzuordnung bekannt")}");
            if (!isController)
            {
                UnmappedDevices.Add(deviceName);
                continue;
            }
            var controller = _sdl.GameControllerOpen(i);
            if (controller != null)
            {
                _controllers.Add((IntPtr)controller);
                Console.WriteLine("[DexHub] Controller verbunden: " +
                    (Marshal.PtrToStringUTF8((IntPtr)_sdl.GameControllerName(controller)) ?? "?"));
            }
        }
    }

    private bool IsPressed(PadControl control)
    {
        if (control == PadControl.None) return false;
        foreach (var ptr in _controllers)
        {
            var c = (GameController*)ptr;
            bool pressed = control switch
            {
                < PadControl.LeftStickUp => _sdl!.GameControllerGetButton(c, (GameControllerButton)(int)control) != 0,
                PadControl.LeftStickUp => Axis(c, 1) < -StickThreshold,
                PadControl.LeftStickDown => Axis(c, 1) > StickThreshold,
                PadControl.LeftStickLeft => Axis(c, 0) < -StickThreshold,
                PadControl.LeftStickRight => Axis(c, 0) > StickThreshold,
                PadControl.LeftTrigger => Axis(c, 4) > TriggerThreshold,
                PadControl.RightTrigger => Axis(c, 5) > TriggerThreshold,
                _ => false,
            };
            if (pressed) return true;
        }
        return false;
    }

    /// <summary>SDL-Achsen: 0/1 = linker Stick X/Y, 2/3 = rechter Stick, 4/5 = Trigger links/rechts.</summary>
    private short Axis(GameController* c, int axis) =>
        _sdl!.GameControllerGetAxis(c, (GameControllerAxis)axis);
}
