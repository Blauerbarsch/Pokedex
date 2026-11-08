namespace DexHub.Core.Emulation;

/// <summary>Zustand eines Eingabegeräts in einem Moment.</summary>
/// <param name="Buttons">Bitmaske der gedrückten Spieltasten (Bit n = JoypadButton n).</param>
/// <param name="FastForward">Schnellvorlauf-Taste gedrückt.</param>
public readonly record struct InputSnapshot(int Buttons, bool FastForward);

/// <summary>Zusätzliche Eingabequelle (z. B. Controller), wird einmal pro Bild abgefragt.</summary>
public interface IInputProvider
{
    InputSnapshot Poll();
}
