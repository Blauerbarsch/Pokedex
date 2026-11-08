using DexHub.Core.Libretro;

namespace DexHub.Core.Settings;

/// <summary>
/// Tasten eines Controllers, nach Position benannt (wie bei SDL / Xbox-Layout).
/// Süd = unten (Xbox A, PlayStation ✕), Ost = rechts (Xbox B, PlayStation ○) usw.
/// </summary>
public enum PadControl
{
    None = -1,
    South = 0, East = 1, West = 2, North = 3,
    Back = 4, Guide = 5, Start = 6,
    LeftStickClick = 7, RightStickClick = 8,
    LeftShoulder = 9, RightShoulder = 10,
    DpadUp = 11, DpadDown = 12, DpadLeft = 13, DpadRight = 14,
    Misc = 15,                                         // Share-/Aufnahme-Taste
    Paddle1 = 16, Paddle2 = 17, Paddle3 = 18, Paddle4 = 19, // Rückseiten-Paddles (Xbox Elite, Steam Deck …)
    Touchpad = 20,                                     // PlayStation-Touchpad drücken
    // Ab hier keine echten Tasten, sondern Achsen
    LeftStickUp = 100, LeftStickDown, LeftStickLeft, LeftStickRight,
    LeftTrigger, RightTrigger,
}

/// <summary>Zusätzliche Aktionen neben den Spieltasten.</summary>
public enum HotkeyAction
{
    FastForward,
    /// <summary>DS-Touchscreen antippen, an der Stelle des Controller-Zeigers (rechter Stick).</summary>
    Touch,
    /// <summary>Schnellspeichern – unabhängig davon, ob das Spiel selbst speichern kann.</summary>
    SaveState,
    LoadState,
}

public sealed class InputSettings
{
    /// <summary>Tastatur: Spieltaste -> Name der Taste (Avalonia "Key"-Name, z. B. "X", "Up", "Return").</summary>
    public Dictionary<JoypadButton, string> Keyboard { get; set; } = [];

    /// <summary>Controller: Spieltaste -> Controller-Taste.</summary>
    public Dictionary<JoypadButton, PadControl> Gamepad { get; set; } = [];

    public Dictionary<HotkeyAction, string> HotkeyKeyboard { get; set; } = [];
    public Dictionary<HotkeyAction, PadControl> HotkeyGamepad { get; set; } = [];

    /// <summary>Linker Stick bewegt zusätzlich das Steuerkreuz.</summary>
    public bool LeftStickAsDpad { get; set; } = true;

    // ---------- Standardbelegung ----------

    /// <summary>
    /// Tastatur: Auf deutschen Tastaturen liegen Y und X nebeneinander unten links – wie B und A beim Game Boy.
    /// </summary>
    public static readonly Dictionary<JoypadButton, string> DefaultKeyboard = new()
    {
        [JoypadButton.Up] = "Up",
        [JoypadButton.Down] = "Down",
        [JoypadButton.Left] = "Left",
        [JoypadButton.Right] = "Right",
        [JoypadButton.A] = "X",
        [JoypadButton.B] = "Y",
        [JoypadButton.X] = "S",
        [JoypadButton.Y] = "A",
        [JoypadButton.L] = "Q",
        [JoypadButton.R] = "W",
        [JoypadButton.Start] = "Return",
        [JoypadButton.Select] = "Back",
    };

    /// <summary>
    /// Controller: nach Position wie bei Nintendo (A rechts, B unten).
    /// Auf einem Xbox-/Steam-Deck-Controller ist "A" (unten) also Nintendo-B.
    /// </summary>
    public static readonly Dictionary<JoypadButton, PadControl> DefaultGamepad = new()
    {
        [JoypadButton.Up] = PadControl.DpadUp,
        [JoypadButton.Down] = PadControl.DpadDown,
        [JoypadButton.Left] = PadControl.DpadLeft,
        [JoypadButton.Right] = PadControl.DpadRight,
        [JoypadButton.A] = PadControl.East,
        [JoypadButton.B] = PadControl.South,
        [JoypadButton.X] = PadControl.North,
        [JoypadButton.Y] = PadControl.West,
        [JoypadButton.L] = PadControl.LeftShoulder,
        [JoypadButton.R] = PadControl.RightShoulder,
        [JoypadButton.Start] = PadControl.Start,
        [JoypadButton.Select] = PadControl.Back,
    };

    public static readonly Dictionary<HotkeyAction, string> DefaultHotkeyKeyboard = new()
    {
        [HotkeyAction.FastForward] = "Space",
        [HotkeyAction.Touch] = "",
        [HotkeyAction.SaveState] = "F5",
        [HotkeyAction.LoadState] = "F9",
    };

    public static readonly Dictionary<HotkeyAction, PadControl> DefaultHotkeyGamepad = new()
    {
        [HotkeyAction.FastForward] = PadControl.RightTrigger,
        [HotkeyAction.Touch] = PadControl.RightStickClick,
        [HotkeyAction.SaveState] = PadControl.None,
        [HotkeyAction.LoadState] = PadControl.None,
    };

    /// <summary>Ergänzt fehlende Einträge (z. B. nach einem Update) mit der Standardbelegung.</summary>
    public void EnsureDefaults()
    {
        foreach (var (k, v) in DefaultKeyboard) Keyboard.TryAdd(k, v);
        foreach (var (k, v) in DefaultGamepad) Gamepad.TryAdd(k, v);
        foreach (var (k, v) in DefaultHotkeyKeyboard) HotkeyKeyboard.TryAdd(k, v);
        foreach (var (k, v) in DefaultHotkeyGamepad) HotkeyGamepad.TryAdd(k, v);
    }

    public void ResetToDefaults()
    {
        Keyboard = new(DefaultKeyboard);
        Gamepad = new(DefaultGamepad);
        HotkeyKeyboard = new(DefaultHotkeyKeyboard);
        HotkeyGamepad = new(DefaultHotkeyGamepad);
        LeftStickAsDpad = true;
    }

    // ---------- Anzeigenamen ----------

    /// <summary>Reihenfolge und Beschriftung der Spieltasten im Einstellungsdialog.</summary>
    public static readonly (JoypadButton Button, string Label)[] ButtonLabels =
    [
        (JoypadButton.Up, "Hoch"),
        (JoypadButton.Down, "Runter"),
        (JoypadButton.Left, "Links"),
        (JoypadButton.Right, "Rechts"),
        (JoypadButton.A, "A"),
        (JoypadButton.B, "B"),
        (JoypadButton.X, "X (DS)"),
        (JoypadButton.Y, "Y (DS)"),
        (JoypadButton.L, "L"),
        (JoypadButton.R, "R"),
        (JoypadButton.Start, "Start"),
        (JoypadButton.Select, "Select"),
    ];

    public static string HotkeyLabel(HotkeyAction action) => action switch
    {
        HotkeyAction.FastForward => "Schnellvorlauf",
        HotkeyAction.Touch => "DS-Touch tippen",
        HotkeyAction.SaveState => "Schnellspeichern",
        HotkeyAction.LoadState => "Schnellladen",
        _ => action.ToString(),
    };

    public static string PadLabel(PadControl c) => c switch
    {
        PadControl.None => "–",
        PadControl.South => "Unten (Xbox A / ✕)",
        PadControl.East => "Rechts (Xbox B / ○)",
        PadControl.West => "Links (Xbox X / □)",
        PadControl.North => "Oben (Xbox Y / △)",
        PadControl.Back => "Back / Select",
        PadControl.Guide => "Home",
        PadControl.Start => "Start",
        PadControl.LeftStickClick => "Linker Stick drücken",
        PadControl.RightStickClick => "Rechter Stick drücken",
        PadControl.LeftShoulder => "LB / L1",
        PadControl.RightShoulder => "RB / R1",
        PadControl.DpadUp => "Steuerkreuz ↑",
        PadControl.DpadDown => "Steuerkreuz ↓",
        PadControl.DpadLeft => "Steuerkreuz ←",
        PadControl.DpadRight => "Steuerkreuz →",
        PadControl.Misc => "Share-Taste",
        PadControl.Paddle1 => "Paddle P1",
        PadControl.Paddle2 => "Paddle P2",
        PadControl.Paddle3 => "Paddle P3",
        PadControl.Paddle4 => "Paddle P4",
        PadControl.Touchpad => "Touchpad",
        PadControl.LeftStickUp => "Linker Stick ↑",
        PadControl.LeftStickDown => "Linker Stick ↓",
        PadControl.LeftStickLeft => "Linker Stick ←",
        PadControl.LeftStickRight => "Linker Stick →",
        PadControl.LeftTrigger => "LT / L2",
        PadControl.RightTrigger => "RT / R2",
        _ => c.ToString(),
    };

    public static string KeyLabel(string key) => key switch
    {
        "" => "–",
        "Up" => "↑",
        "Down" => "↓",
        "Left" => "←",
        "Right" => "→",
        "Return" or "Enter" => "Enter",
        "Back" => "Rücktaste",
        "Space" => "Leertaste",
        "LeftShift" => "Shift links",
        "RightShift" => "Shift rechts",
        "LeftCtrl" => "Strg links",
        "RightCtrl" => "Strg rechts",
        _ => key,
    };
}
