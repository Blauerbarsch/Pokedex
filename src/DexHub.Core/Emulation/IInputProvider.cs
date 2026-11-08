using DexHub.Core.Settings;

namespace DexHub.Core.Emulation;

/// <summary>Zustand eines Eingabegeräts in einem Moment.</summary>
/// <param name="Buttons">Bitmaske der gedrückten Spieltasten (Bit n = JoypadButton n).</param>
/// <param name="Hotkeys">Bitmaske der gedrückten Zusatztasten (Bit n = HotkeyAction n).</param>
/// <param name="CursorX">Rechter Stick waagerecht, -1 bis 1 (bewegt den Touch-Zeiger).</param>
/// <param name="CursorY">Rechter Stick senkrecht, -1 bis 1.</param>
public readonly record struct InputSnapshot(int Buttons, int Hotkeys = 0, float CursorX = 0, float CursorY = 0)
{
    public bool Has(HotkeyAction action) => (Hotkeys & (1 << (int)action)) != 0;
}

/// <summary>Zusätzliche Eingabequelle (z. B. Controller), wird einmal pro Bild abgefragt.</summary>
public interface IInputProvider
{
    InputSnapshot Poll();
}
