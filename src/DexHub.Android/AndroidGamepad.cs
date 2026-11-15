using Android.Views;
using DexHub.Core.Emulation;
using DexHub.Core.Libretro;
using DexHub.Core.Settings;
using DexHub.UI;

namespace DexHub.Mobile;

/// <summary>
/// Controller unter Android (Bluetooth-Controller, eingebaute Tasten von Handhelds wie Retroid/Ayn/Odin).
/// Tasten werden wie am PC nach Position erfasst (Süd/Ost/West/Nord …) und über die Belegung aus
/// "Steuerung" auf Spieltasten gelegt – die Seite funktioniert also genauso wie am PC.
/// </summary>
public sealed class AndroidGamepad : IInputProvider, IGamepadInfo
{
    public static readonly AndroidGamepad Instance = new();

    /// <summary>Während ein Spiel läuft, gehen die Tasten nur an den Emulator (sonst steuern sie die Oberfläche).</summary>
    public static bool Active { get; set; }

    /// <summary>Belegung (wird beim Spielstart gesetzt).</summary>
    public static InputSettings? Settings { get; set; }

    public bool StickAsDpad { get; set; } = true;

    /// <summary>Eine Controller-Taste wurde im Spiel gedrückt (Bildschirmtasten ausblenden).</summary>
    public static event Action? Used;

    private static readonly object Gate = new();
    private static readonly HashSet<PadControl> Pressed = [];
    private static float _lx, _ly, _rx, _ry, _lt, _rt, _hatX, _hatY;

    private static readonly Dictionary<Keycode, PadControl> Keys = new()
    {
        [Keycode.ButtonA] = PadControl.South,
        [Keycode.ButtonB] = PadControl.East,
        [Keycode.ButtonX] = PadControl.West,
        [Keycode.ButtonY] = PadControl.North,
        [Keycode.ButtonSelect] = PadControl.Back,
        [Keycode.ButtonMode] = PadControl.Guide,
        [Keycode.ButtonStart] = PadControl.Start,
        [Keycode.ButtonThumbl] = PadControl.LeftStickClick,
        [Keycode.ButtonThumbr] = PadControl.RightStickClick,
        [Keycode.ButtonL1] = PadControl.LeftShoulder,
        [Keycode.ButtonR1] = PadControl.RightShoulder,
        [Keycode.ButtonL2] = PadControl.LeftTrigger,
        [Keycode.ButtonR2] = PadControl.RightTrigger,
        [Keycode.DpadUp] = PadControl.DpadUp,
        [Keycode.DpadDown] = PadControl.DpadDown,
        [Keycode.DpadLeft] = PadControl.DpadLeft,
        [Keycode.DpadRight] = PadControl.DpadRight,
    };

    /// <summary>Taste gedrückt/losgelassen. true = an den Emulator gegangen (nicht an die Oberfläche weitergeben).</summary>
    public static bool Handle(KeyEvent e)
    {
        if (!Keys.TryGetValue(e.KeyCode, out var control)) return false;
        if (e.Action is not (KeyEventActions.Down or KeyEventActions.Up)) return Active;
        lock (Gate)
        {
            if (e.Action == KeyEventActions.Down) Pressed.Add(control);
            else Pressed.Remove(control);
        }
        if (Active && e.Action == KeyEventActions.Down) Used?.Invoke();
        return Active;
    }

    /// <summary>Sticks, Steuerkreuz als Achse, analoge Schultertasten.</summary>
    public static bool HandleMotion(MotionEvent e)
    {
        if ((e.Source & (InputSourceType.Joystick | InputSourceType.Gamepad)) == 0) return false;
        lock (Gate)
        {
            _lx = e.GetAxisValue(Axis.X);
            _ly = e.GetAxisValue(Axis.Y);
            _rx = e.GetAxisValue(Axis.Z);
            _ry = e.GetAxisValue(Axis.Rz);
            _hatX = e.GetAxisValue(Axis.HatX);
            _hatY = e.GetAxisValue(Axis.HatY);
            _lt = Math.Max(e.GetAxisValue(Axis.Ltrigger), e.GetAxisValue(Axis.Brake));
            _rt = Math.Max(e.GetAxisValue(Axis.Rtrigger), e.GetAxisValue(Axis.Gas));
        }
        return Active;
    }

    private static bool IsPressed(PadControl control)
    {
        const float dead = 0.5f;
        return control switch
        {
            PadControl.None => false,
            PadControl.LeftStickUp => _ly < -dead,
            PadControl.LeftStickDown => _ly > dead,
            PadControl.LeftStickLeft => _lx < -dead,
            PadControl.LeftStickRight => _lx > dead,
            PadControl.LeftTrigger => _lt > dead || Pressed.Contains(PadControl.LeftTrigger),
            PadControl.RightTrigger => _rt > dead || Pressed.Contains(PadControl.RightTrigger),
            PadControl.DpadUp => _hatY < -dead || Pressed.Contains(control),
            PadControl.DpadDown => _hatY > dead || Pressed.Contains(control),
            PadControl.DpadLeft => _hatX < -dead || Pressed.Contains(control),
            PadControl.DpadRight => _hatX > dead || Pressed.Contains(control),
            _ => Pressed.Contains(control),
        };
    }

    public static void Reset()
    {
        lock (Gate)
        {
            Pressed.Clear();
            _lx = _ly = _rx = _ry = _lt = _rt = _hatX = _hatY = 0;
        }
    }

    public InputSnapshot Poll()
    {
        var settings = Settings;
        if (settings is null) return default;
        lock (Gate)
        {
            int buttons = 0;
            foreach (var (button, control) in settings.Gamepad)
                if (IsPressed(control)) buttons |= 1 << (int)button;
            if (settings.LeftStickAsDpad && StickAsDpad)
            {
                if (IsPressed(PadControl.LeftStickUp)) buttons |= 1 << (int)JoypadButton.Up;
                if (IsPressed(PadControl.LeftStickDown)) buttons |= 1 << (int)JoypadButton.Down;
                if (IsPressed(PadControl.LeftStickLeft)) buttons |= 1 << (int)JoypadButton.Left;
                if (IsPressed(PadControl.LeftStickRight)) buttons |= 1 << (int)JoypadButton.Right;
            }
            int hotkeys = 0;
            foreach (var (action, control) in settings.HotkeyGamepad)
                if (IsPressed(control)) hotkeys |= 1 << (int)action;

            static short S(float v) => (short)Math.Clamp(v * 32767f, -32767f, 32767f);
            return new InputSnapshot(buttons, hotkeys, _rx, _ry, S(_lx), S(_ly), S(_rx), S(_ry));
        }
    }

    // ---------- für die Seite "Steuerung" ----------

    public IReadOnlyList<string> ConnectedNames()
    {
        var names = new List<string>();
        foreach (var id in InputDevice.GetDeviceIds() ?? [])
        {
            var device = InputDevice.GetDevice(id);
            if (device is null) continue;
            var sources = device.Sources;
            if ((sources & InputSourceType.Gamepad) == InputSourceType.Gamepad || (sources & InputSourceType.Joystick) == InputSourceType.Joystick)
                names.Add(device.Name ?? "Controller");
        }
        return names;
    }

    public string? InitError => null;
    public IReadOnlyList<string> UnmappedDevices => [];

    public PadControl FirstPressed()
    {
        lock (Gate)
            foreach (var control in Enum.GetValues<PadControl>())
                if (IsPressed(control)) return control;
        return PadControl.None;
    }
}
