using Avalonia.Controls;
using DexHub.UI;

namespace DexHub.App;

/// <summary>Hauptfenster am PC: enthält die gemeinsame Oberfläche (DexHub.UI.MainView).</summary>
public sealed class MainWindow : Window
{
    public MainWindow()
    {
        MainView.Host = DesktopPlatform.Instance;
        Title = "DexHub";
        Width = 1280;
        Height = 820;
        MinWidth = 640;
        MinHeight = 520;
        Content = new MainView();
    }
}
