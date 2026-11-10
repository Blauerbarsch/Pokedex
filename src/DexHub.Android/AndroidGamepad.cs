using Android.Views;
using DexHub.Core.Emulation;
using DexHub.Core.Libretro;
using DexHub.Core.Settings;

namespace DexHub.Mobile;

/// <summary>
/// Controller-Eingaben unter Android (Bluetooth-Controller, eingebaute Tasten von Handhelds).
/// Belegung nach Position wie bei Nintendo: unten = B, rechts = A, links = Y, oben = X.
/// R2 = Vorspulen, L2 = Touch (DS).
/// </summary>
public sealed class AndroidGamepad : IInputProvider
{
    public static readonly AndroidGamepad Instance = new();

    /// <summary>Nur während ein Spiel läuft, gehen Tasten an den Emulator (sonst steuern sie die Oberfläche).</summary>
    public static bool Active { get; set; }

    private static int _buttons, _hotkeys, _axisButtons;
    private static float _cursorX, _cursorY;

    private static readonly Dictionary<Keycode, JoypadButton> Map = new()
    {
        [Keycode.ButtonA] = JoypadButton.B,
        [Keycode.ButtonB] = JoypadButton.A,
        [Keycode.ButtonX] = JoypadButton.Y,
        [Keycode.ButtonY] = JoypadButton.X,
        [Keycode.ButtonL1] = JoypadButton.L,
        [Keycode.ButtonR1] = JoypadButton.R,
        [Keycode.ButtonStart] = JoypadButton.Start,
        [Keycode.ButtonSelect] = JoypadButton.Select,
        [Keycode.DpadUp] = JoypadButton.Up,
        [Keycode.DpadDown] = JoypadButton.Down,
        [Keycode.DpadLeft] = JoypadButton.Left,
        [Keycode.DpadRight] = JoypadButton.Right,
    };

    private static readonly Dictionary<Keycode, HotkeyAction> HotkeyMap = new()
    {
        [Keycode.ButtonR2] = HotkeyAction.FastForward,
        [Keycode.ButtonL2] = HotkeyAction.Touch,
        [Keycode.ButtonThumbr] = HotkeyAction.Touch,
    };

    public static bool Handle(KeyEvent e)
    {
        bool down = e.Action == KeyEventActions.Down;
        if (e.Action is not (KeyEventActions.Down or KeyEventActions.Up)) return false;

        if (Map.TryGetValue(e.KeyCode, out var button))
        {
            Set(ref _buttons, 1 << (int)button, down);
            return true;
        }
        if (HotkeyMap.TryGetValue(e.KeyCode, out var hotkey))
        {
            Set(ref _hotkeys, 1 << (int)hotkey, down);
            return true;
        }
        return false;
    }

    public static bool HandleMotion(MotionEvent e)
    {
        if ((e.Source & (InputSourceType.Joystick | InputSourceType.Gamepad)) == 0) return false;
        float x = e.GetAxisValue(Axis.X), y = e.GetAxisValue(Axis.Y);
        float hatX = e.GetAxisValue(Axis.HatX), hatY = e.GetAxisValue(Axis.HatY);
        const float dead = 0.5f;

        int axis = 0;
        if (x < -dead || hatX < -dead) axis |= 1 << (int)JoypadButton.Left;
        if (x > dead || hatX > dead) axis |= 1 << (int)JoypadButton.Right;
        if (y < -dead || hatY < -dead) axis |= 1 << (int)JoypadButton.Up;
        if (y > dead || hatY > dead) axis |= 1 << (int)JoypadButton.Down;
        Volatile.Write(ref _axisButtons, axis);

        // Rechter Stick bewegt den Touch-Zeiger (DS)
        float rx = e.GetAxisValue(Axis.Z), ry = e.GetAxisValue(Axis.Rz);
        _cursorX = Math.Abs(rx) > 0.15f ? rx : 0;
        _cursorY = Math.Abs(ry) > 0.15f ? ry : 0;

        // Analoge Schultertasten als Vorspulen
        Set(ref _hotkeys, 1 << (int)HotkeyAction.FastForward, e.GetAxisValue(Axis.Rtrigger) > 0.5f || e.GetAxisValue(Axis.Gas) > 0.5f);
        return true;
    }

    private static void Set(ref int field, int bit, bool on)
    {
        int old, updated;
        do
        {
            old = field;
            updated = on ? old | bit : old & ~bit;
        } while (Interlocked.CompareExchange(ref field, updated, old) != old);
    }

    public static void Reset()
    {
        _buttons = _hotkeys = _axisButtons = 0;
        _cursorX = _cursorY = 0;
    }

    public InputSnapshot Poll() =>
        new(Volatile.Read(ref _buttons) | Volatile.Read(ref _axisButtons), Volatile.Read(ref _hotkeys), _cursorX, _cursorY);
}
