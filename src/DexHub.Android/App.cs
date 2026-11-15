using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using DexHub.UI;

namespace DexHub.Mobile;

public class App : Avalonia.Application
{
    public static AndroidShell? Shell { get; private set; }

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://DexHub.Mobile/")) { Source = new Uri("avares://DexHub.UI/Theme/DexTheme.axaml") });
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            Shell = new AndroidShell();
            MainView.Host = new AndroidPlatform(Shell);
            Shell.ShowMain(new MainView());
            single.MainView = Shell;
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Zurück-Taste: erst das Spiel schließen, dann wie gewohnt die App.</summary>
    public static bool HandleBack() => Shell?.HandleBack() ?? false;
}
