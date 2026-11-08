using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using DexHub.Core.Emulation;
using DexHub.Core.Libretro;
using DexHub.Core.Settings;
using GamePlatform = DexHub.Core.Models.Platform;

namespace DexHub.App;

/// <summary>Fenster, in dem ein Spiel läuft.</summary>
public partial class GameWindow : Window
{
    public const string ControlsHelp =
        "Steuerung: Pfeiltasten · X = A · Y = B · Enter = Start · Rücktaste = Select · Leertaste halten = Schnellvorlauf · " +
        "P = Pause · F5/F9 = Schnellspeichern/-laden · F11 = Vollbild · DS-Touch: Maus/Finger oder rechter Stick + Stick drücken  " +
        "(Belegung änderbar im Tab Steuerung)";

    private readonly AppSettings _settings;
    private readonly EmulatorSession _session;
    private WriteableBitmap? _bitmap;
    private long _lastFrameId = -1;
    private bool _closed;
    private DispatcherTimer? _osdTimer;

    // Nur für den XAML-Designer der IDE
    public GameWindow() : this(null!, null!) { }

    public GameWindow(EmulatorSession session, AppSettings settings)
    {
        InitializeComponent();
        _session = session;
        _settings = settings;
        if (session is null) return;

        ApplySettings();

        Title = $"{session.Game.Title} – DexHub";
        if (session.Game.Info.Platform == GamePlatform.NintendoDS)
        {
            session.UseTouchCursor = true;
            // Zwei Bildschirme übereinander
            Width = 540;
            Height = 800;
        }

        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        Deactivated += (_, _) => ReleaseAllKeys();

        session.Message += msg => Dispatcher.UIThread.Post(() => ShowOsd(msg));
        session.Failed += msg => Dispatcher.UIThread.Post(() => ShowOsd("Fehler: " + msg, persistent: true));

        Opened += (_, _) =>
        {
            ShowOsd(ControlsHelp);
            session.Start();
            RequestAnimationFrame(OnAnimationFrame);
        };

        Closed += (_, _) =>
        {
            _closed = true;
            _session.Dispose(); // hält den Emulator an und speichert den Spielstand
        };
    }

    // =====================================================================
    // Bild
    // =====================================================================

    /// <summary>Wird bei jedem Bildschirm-Refresh aufgerufen und holt das neueste Spielbild.</summary>
    private void OnAnimationFrame(TimeSpan _)
    {
        if (_closed) return;

        var (w, h) = _session.FrameSize;
        if (w > 0 && h > 0)
        {
            if (_bitmap is null || _bitmap.PixelSize.Width != w || _bitmap.PixelSize.Height != h)
            {
                _bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                    PixelFormat.Bgra8888, AlphaFormat.Opaque);
                Screen.Source = _bitmap;
                _lastFrameId = -1;
            }

            bool updated;
            using (var fb = _bitmap.Lock())
                updated = _session.CopyFrame(ref _lastFrameId, fb.Address, fb.RowBytes, w, h);

            if (updated) Screen.InvalidateVisual();
            UpdateTouchCursor(w, h);
        }

        RequestAnimationFrame(OnAnimationFrame);
    }

    /// <summary>Zeichnet den Controller-Zeiger an die passende Stelle über dem Spielbild.</summary>
    private void UpdateTouchCursor(int w, int h)
    {
        var (x, y, visible, pressed) = _session.TouchCursor;
        TouchCursor.IsVisible = visible;
        if (!visible) return;

        var bounds = Screen.Bounds;
        double scale = Math.Min(bounds.Width / w, bounds.Height / h);
        double left = bounds.X + (bounds.Width - w * scale) / 2 + x * w * scale;
        double top = bounds.Y + (bounds.Height - h * scale) / 2 + y * h * scale;

        Canvas.SetLeft(TouchCursor, left - TouchCursor.Width / 2);
        Canvas.SetTop(TouchCursor, top - TouchCursor.Height / 2);
        TouchCursor.Opacity = pressed ? 1.0 : 0.75;
    }

    // =====================================================================
    // Eingabe
    // =====================================================================

    /// <summary>Übernimmt Lautstärke, Skalierung usw. – auch während das Spiel läuft.</summary>
    public void ApplySettings()
    {
        _session.Volume = _settings.Volume / 100f;
        _session.FastForwardSpeed = _settings.FastForwardSpeed;
        RenderOptions.SetBitmapInterpolationMode(Screen,
            _settings.SmoothScaling ? BitmapInterpolationMode.HighQuality : BitmapInterpolationMode.None);
    }

    /// <summary>Welche Spieltaste liegt auf dieser Tastaturtaste? (laut Einstellungen)</summary>
    private bool TryMapKey(Key key, out JoypadButton button)
    {
        foreach (var (b, keyName) in _settings.Input.Keyboard)
        {
            if (Enum.TryParse<Key>(keyName, out var bound) && bound == key)
            {
                button = b;
                return true;
            }
        }
        button = default;
        return false;
    }

    private bool IsHotkey(Key key, HotkeyAction action) =>
        _settings.Input.HotkeyKeyboard.TryGetValue(action, out var name)
        && Enum.TryParse<Key>(name, out var bound) && bound == key;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F5:
                _session.RequestSaveState();
                break;
            case Key.F9:
                _session.RequestLoadState();
                break;
            case Key.F11:
                WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
                break;
            case Key.Escape when WindowState == WindowState.FullScreen:
                WindowState = WindowState.Normal;
                break;
            case Key.P:
                _session.TogglePause();
                ShowOsd(_session.IsPaused ? "Pausiert (P zum Fortsetzen)" : "Weiter", persistent: _session.IsPaused);
                break;
            default:
                bool handled = false;
                if (TryMapKey(e.Key, out var button)) { _session.SetButton(button, true); handled = true; }
                if (IsHotkey(e.Key, HotkeyAction.FastForward)) { _session.SetFastForward(true); handled = true; }
                if (IsHotkey(e.Key, HotkeyAction.Touch)) { _session.SetKeyboardTouch(true); handled = true; }
                if (!handled) return;
                break;
        }
        e.Handled = true;
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (TryMapKey(e.Key, out var button))
        {
            _session.SetButton(button, false);
            e.Handled = true;
        }
        if (IsHotkey(e.Key, HotkeyAction.FastForward))
        {
            _session.SetFastForward(false);
            e.Handled = true;
        }
        if (IsHotkey(e.Key, HotkeyAction.Touch))
        {
            _session.SetKeyboardTouch(false);
            e.Handled = true;
        }
    }

    /// <summary>Fenster verliert den Fokus: alle Tasten loslassen, damit nichts "hängen" bleibt.</summary>
    private void ReleaseAllKeys()
    {
        foreach (var b in Enum.GetValues<JoypadButton>()) _session.SetButton(b, false);
        _session.SetFastForward(false);
        _session.SetKeyboardTouch(false);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e) => UpdatePointer(e, true);
    private void OnPointerMoved(object? sender, PointerEventArgs e) =>
        UpdatePointer(e, e.GetCurrentPoint(Screen).Properties.IsLeftButtonPressed);
    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e) => UpdatePointer(e, false);

    /// <summary>Rechnet die Mausposition in Koordinaten auf dem Spielbild um (für den DS-Touchscreen).</summary>
    private void UpdatePointer(PointerEventArgs e, bool pressed)
    {
        var (w, h) = _session.FrameSize;
        if (w == 0 || h == 0) return;

        var bounds = Screen.Bounds;
        double scale = Math.Min(bounds.Width / w, bounds.Height / h);
        double offsetX = (bounds.Width - w * scale) / 2;
        double offsetY = (bounds.Height - h * scale) / 2;

        var pos = e.GetPosition(Screen);
        double x = (pos.X - offsetX) / (w * scale);
        double y = (pos.Y - offsetY) / (h * scale);

        bool inside = x is >= 0 and <= 1 && y is >= 0 and <= 1;
        _session.SetPointer(x, y, pressed && inside);
    }

    // =====================================================================
    // Einblendungen
    // =====================================================================

    private void ShowOsd(string text, bool persistent = false)
    {
        OsdText.Text = text;
        OsdBorder.IsVisible = true;

        _osdTimer?.Stop();
        if (persistent) return;

        _osdTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(text == ControlsHelp ? 8 : 3) };
        _osdTimer.Tick += (_, _) =>
        {
            OsdBorder.IsVisible = false;
            _osdTimer?.Stop();
        };
        _osdTimer.Start();
    }
}
