using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace DexHub.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (Program.Play is { } play)
            {
                // Dieser Prozess zeigt nur ein Spiel
                try
                {
                    desktop.MainWindow = PlayerHost.CreateWindow(play);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[DexHub] Spiel konnte nicht gestartet werden: " + ex);
                    desktop.MainWindow = new Window
                    {
                        Title = "DexHub – Fehler",
                        Width = 520,
                        Height = 200,
                        Content = new TextBlock
                        {
                            Text = "Spiel konnte nicht gestartet werden:\n\n" + ex.Message,
                            Margin = new Thickness(20),
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        },
                    };
                }
            }
            else
            {
                desktop.MainWindow = new MainWindow();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
