using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

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
        // Schmal (Handy hochkant) oder niedrig (Handy quer): keine Seitenleiste
        bool compact = Bounds.Width > 0 && (Bounds.Width < CompactBelow || Bounds.Height < 560);
        LibrarySubtitle.IsVisible = Bounds.Height <= 0 || Bounds.Height >= 520; // Querformat am Handy: Platz sparen
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

        // Details erst zeigen, wenn etwas ausgewählt ist; Hinweis in der Spielen-Leiste weglassen
        DexDetail.IsVisible = !compact || _selectedDexId != 0;
        StatsDetail.IsVisible = !compact;
        SelectedGameText.IsVisible = !compact;
        if (_ready) RefreshGamesList(); // Kachelgröße

        // Bibliothek: Suche über volle Breite, Knöpfe darunter
        LibrarySearchBox.Width = compact ? double.NaN : 320;
        LibrarySearchBox.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        Grid.SetColumnSpan(LibrarySearchBox, compact ? 2 : 1);
        Grid.SetRow(LibraryButtons, compact ? 1 : 0);
        Grid.SetColumn(LibraryButtons, compact ? 0 : 1);
        LibraryButtons.Margin = compact ? new Thickness(0, 8, 0, 0) : default;
        // Einstellungen „Ton & Spielen“: schmalere Beschriftung, Auswahlfelder über die Restbreite
        SoundGrid.ColumnDefinitions = new ColumnDefinitions(compact ? "112,*" : "180,*");
        SoundGrid.RowDefinitions = new RowDefinitions(compact ? "48,48,48,Auto" : "44,44,44,44");
        VolumeSlider.Width = compact ? 140 : 260;
        FastForwardBox.Width = compact ? double.NaN : 160;
        FastForwardModeBox.Width = compact ? double.NaN : 320;
        FastForwardBox.HorizontalAlignment = FastForwardModeBox.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;

        PlayFreshButton.Content = compact ? "Neu starten" : "Ohne Fortsetzen starten";

        EnsureRealTabSelected();
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

    /// <summary>Falls die Auswahl verloren ging oder auf einer Abschnitts-Überschrift liegt: erste echte Seite wählen.</summary>
    private void EnsureRealTabSelected()
    {
        if (MainTabs.SelectedItem is TabItem { IsEnabled: true } t && !t.Classes.Contains("group")) return;
        foreach (var item in MainTabs.Items)
            if (item is TabItem { IsEnabled: true } tab && !tab.Classes.Contains("group"))
            {
                MainTabs.SelectedItem = tab;
                return;
            }
    }

    private void OnCompactProfileChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || CompactProfileBox.SelectedItem is not string profile || profile == _settings.CurrentProfile) return;
        ProfileBox.SelectedItem = profile; // löst den normalen Wechsel aus
    }
}
