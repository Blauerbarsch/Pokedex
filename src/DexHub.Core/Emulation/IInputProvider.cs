using DexHub.Core.Settings;

namespace DexHub.Core.Emulation;

/// <summary>Zustand eines Eingabegeräts in einem Moment.</summary>
/// <param name="Buttons">Bitmaske der gedrückten Spieltasten (Bit n = JoypadButton n).</param>
/// <param name="Hotkeys">Bitmaske der gedrückten Zusatztasten (Bit n = HotkeyAction n).</param>
/// <param name="CursorX">Rechter Stick waagerecht, -1 bis 1 (bewegt den Touch-Zeiger).</param>
/// <param name="CursorY">Rechter Stick senkrecht, -1 bis 1.</param>
/// <param name="LeftX">Linker Stick (N64/GameCube), -32767 bis 32767.</param>
public readonly record struct InputSnapshot(int Buttons, int Hotkeys = 0, float CursorX = 0, float CursorY = 0,
    short LeftX = 0, short LeftY = 0, short RightX = 0, short RightY = 0)
{
    public bool Has(HotkeyAction action) => (Hotkeys & (1 << (int)action)) != 0;
}

/// <summary>Zusätzliche Eingabequelle (z. B. Controller), wird einmal pro Bild abgefragt.</summary>
public interface IInputProvider
{
    InputSnapshot Poll();

    /// <summary>
    /// false bei Konsolen mit Analog-Stick (N64, GameCube …): dann steuert der linke Stick nicht zusätzlich das Steuerkreuz.
    /// </summary>
    bool StickAsDpad { get; set; }
}
