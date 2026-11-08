using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DexHub.Core.Settings;

namespace DexHub.App;

/// <summary>Zeichnet Scanlines oder ein LCD-Raster passend zu den Spielpixeln über das Bild.</summary>
public sealed class EffectOverlay : Control
{
    private OverlayEffect _effect;
    private List<(PixelRect Source, Rect Dest)> _screens = [];

    private static readonly IBrush ScanlineBrush = new SolidColorBrush(Color.FromArgb(100, 0, 0, 0));
    private static readonly IBrush GridBrush = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0));

    /// <summary>Neu zeichnen nur, wenn sich etwas geändert hat.</summary>
    public void Update(OverlayEffect effect, List<(PixelRect Source, Rect Dest)> screens)
    {
        bool changed = effect != _effect || screens.Count != _screens.Count;
        for (int i = 0; !changed && i < screens.Count; i++)
            changed = screens[i] != _screens[i];
        if (!changed) return;

        _effect = effect;
        _screens = screens;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_effect == OverlayEffect.None) return;

        foreach (var (source, dest) in _screens)
        {
            double rowHeight = dest.Height / source.Height;
            double colWidth = dest.Width / source.Width;
            if (rowHeight < 2) continue; // bei zu kleiner Darstellung sieht der Effekt nur matschig aus

            if (_effect == OverlayEffect.Scanlines)
            {
                // Untere ~40 % jeder Pixelzeile abdunkeln
                for (int y = 0; y < source.Height; y++)
                    context.FillRectangle(ScanlineBrush,
                        new Rect(dest.X, dest.Y + y * rowHeight + rowHeight * 0.6, dest.Width, rowHeight * 0.4));
            }
            else
            {
                // Dünne Linien zwischen allen Pixeln, wie beim Handheld-Display
                for (int y = 1; y < source.Height; y++)
                    context.FillRectangle(GridBrush,
                        new Rect(dest.X, dest.Y + y * rowHeight - rowHeight * 0.1, dest.Width, rowHeight * 0.2));
                for (int x = 1; x < source.Width; x++)
                    context.FillRectangle(GridBrush,
                        new Rect(dest.X + x * colWidth - colWidth * 0.1, dest.Y, colWidth * 0.2, dest.Height));
            }
        }
    }
}
