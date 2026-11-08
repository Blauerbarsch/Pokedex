namespace DexHub.Core.Emulation;

/// <summary>Zustand eines Eingabegeräts in einem Moment.</summary>
/// <param name="Buttons">Bitmaske der gedrückten Spieltasten (Bit n = JoypadButton n).</param>
/// <param name="FastForward">Schnellvorlauf-Taste gedrückt.</param>
/// <param name="CursorX">Rechter Stick waagerecht, -1 bis 1 (bewegt den Touch-Zeiger).</param>
/// <param name="CursorY">Rechter Stick senkrecht, -1 bis 1.</param>
/// <param name="Touch">Touch-Taste gedrückt.</param>
public readonly record struct InputSnapshot(
    int Buttons, bool FastForward, float CursorX = 0, float CursorY = 0, bool Touch = false);

/// <summary>Zusätzliche Eingabequelle (z. B. Controller), wird einmal pro Bild abgefragt.</summary>
public interface IInputProvider
{
    InputSnapshot Poll();
}
