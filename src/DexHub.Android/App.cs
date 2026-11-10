using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace DexHub.Mobile;

public class App : Application
{
    public static MobileView? View { get; private set; }

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime single)
            single.MainView = View = new MobileView();
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Zurück-Taste: erst das Spiel schließen, dann wie gewohnt die App.</summary>
    public static bool HandleBack() => View?.HandleBack() ?? false;
}
