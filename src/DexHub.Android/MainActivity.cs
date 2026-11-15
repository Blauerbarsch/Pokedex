using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Avalonia;
using Avalonia.Android;
using DexHub.Core.Emulation;

namespace DexHub.Mobile;

[Activity(Name = "de.blauerbarsch.dexhub.MainActivity", Label = "DexHub", Theme = "@style/Theme.AppCompat.NoActionBar", MainLauncher = true, Exported = true,
    ScreenOrientation = ScreenOrientation.FullUser,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode
                           | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation)]
public class MainActivity : AvaloniaMainActivity<App>
{
    public static MainActivity? Current { get; private set; }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder).WithInterFont();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        Current = this;
        CoreCatalog.AndroidLibraryFolder = ApplicationInfo?.NativeLibraryDir;
        // ROMs liegen im App-Ordner auf dem gemeinsamen Speicher (per USB/Dateimanager erreichbar:
        // Android/data/de.blauerbarsch.dexhub/files/roms) – dorthin kopiert auch "ROMs hinzufügen".
        AndroidPlatform.RomFolder = System.IO.Path.Combine(GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir!.AbsolutePath, "roms");
        AndroidPlatform.Autostart = Intent?.GetStringExtra("autostart");
        DexHub.Core.Libretro.LibretroCore.Trace = AndroidPlatform.Autostart is not null; // automatischer Test: alles protokollieren
        base.OnCreate(savedInstanceState);
        BackRequested += (_, e) => e.Handled = App.HandleBack();
    }

    protected override void OnPause()
    {
        App.Shell?.SetBackground(true);
        base.OnPause();
    }

    protected override void OnResume()
    {
        base.OnResume();
        App.Shell?.SetBackground(false);
    }

    /// <summary>Android blendet die Leisten z. B. nach Drehen oder Benachrichtigungen wieder ein – erneut ausblenden.</summary>
    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus && _immersive) SetImmersive(true);
    }

    /// <summary>Controller-Tasten gehen direkt an den Emulator, solange ein Spiel läuft.</summary>
    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e is not null && AndroidGamepad.Active && AndroidGamepad.Handle(e)) return true;
        return base.DispatchKeyEvent(e);
    }

    /// <summary>Analog-Sticks und Steuerkreuz (bei manchen Controllern als Achsen gemeldet).</summary>
    public override bool DispatchGenericMotionEvent(MotionEvent? e)
    {
        if (e is not null && AndroidGamepad.Active && AndroidGamepad.HandleMotion(e)) return true;
        return base.DispatchGenericMotionEvent(e);
    }

    /// <summary>Im Spiel den ganzen Bildschirm nutzen (Statusleiste ausblenden).</summary>
    private bool _immersive;

    public void SetImmersive(bool on)
    {
        _immersive = on;
        if (Window is null) return;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R && Window.InsetsController is { } insets)
        {
            if (on)
            {
                insets.Hide(WindowInsets.Type.SystemBars());
                insets.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
            }
            else insets.Show(WindowInsets.Type.SystemBars());
        }
        if (on) Window.AddFlags(WindowManagerFlags.KeepScreenOn);
        else Window.ClearFlags(WindowManagerFlags.KeepScreenOn);
    }
}
