using Avalonia;
using Avalonia.Controls;

namespace DexHub.UI;

/// <summary>
/// Anpassung an schmale Bildschirme (Handy, kleines Fenster): statt der Seitenleiste eine rote Kopfleiste
/// mit Benutzerwahl und darunter eine wischbare Reiterleiste; zweispaltige Seiten werden untereinander gelegt.
/// </summary>
public partial class MainView
{
    private bool? _compact;

    /// <summary>Ab dieser Breite (in Avalonia-Einheiten) gibt es die Seitenleiste.</summary>
    private const double CompactBelow = 860;

    private void UpdateCompactLayout()
    {
        bool compact = Bounds.Width > 0 && Bounds.Width < CompactBelow;
        if (compact == _compact) return;
        _compact = compact;

        Classes.Set("compact", compact);
        CompactHeader.IsVisible = compact;
        SidebarBackground.IsVisible = !compact;
        SidebarHeader.IsVisible = !compact;
        TrainerCard.IsVisible = !compact;

        Stack(DexGrid, DexDetail, compact, 320, hasSearchRow: true, "*,300");
        Stack(StatsGrid, StatsDetail, compact, 300, hasSearchRow: false, "*,330");
        StackGuide(compact);
    }

    /// <summary>Zweite Spalte (Details) unter den Hauptinhalt legen bzw. zurück daneben.</summary>
    private static void Stack(Grid grid, Control side, bool compact, double sideHeight, bool hasSearchRow, string wideColumns)
    {
        if (compact)
        {
            grid.ColumnDefinitions = new ColumnDefinitions("*");
            grid.RowDefinitions = new RowDefinitions(hasSearchRow ? "Auto,*,Auto" : "*,Auto");
            Grid.SetColumn(side, 0);
            Grid.SetRow(side, hasSearchRow ? 2 : 1);
            side.MaxHeight = sideHeight;
            side.Margin = new Thickness(0, 8, 0, 0);
        }
        else
        {
            grid.ColumnDefinitions = new ColumnDefinitions(wideColumns);
            grid.RowDefinitions = new RowDefinitions(hasSearchRow ? "Auto,*" : "*");
            Grid.SetColumn(side, 1);
            Grid.SetRow(side, hasSearchRow ? 1 : 0);
            side.MaxHeight = double.PositiveInfinity;
            side.Margin = new Thickness(0, hasSearchRow ? 8 : 0, 0, 0);
        }
    }

    private void StackGuide(bool compact)
    {
        if (compact)
        {
            GuideGrid.ColumnDefinitions = new ColumnDefinitions("*");
            GuideGrid.RowDefinitions = new RowDefinitions("180,*");
            Grid.SetRow(GuideScroll, 1);
            Grid.SetColumn(GuideScroll, 0);
            GuideScroll.Margin = new Thickness(0, 8, 0, 0);
        }
        else
        {
            GuideGrid.ColumnDefinitions = new ColumnDefinitions("260,*");
            GuideGrid.RowDefinitions = new RowDefinitions("*");
            Grid.SetRow(GuideScroll, 0);
            Grid.SetColumn(GuideScroll, 1);
            GuideScroll.Margin = new Thickness(12, 0, 0, 0);
        }
    }

    private void OnCompactProfileChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || CompactProfileBox.SelectedItem is not string profile || profile == _settings.CurrentProfile) return;
        ProfileBox.SelectedItem = profile; // löst den normalen Wechsel aus
    }
}
