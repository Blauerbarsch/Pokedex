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
        "Steuerung: Pfeiltasten · X = A · Y = B · Enter = Start · Rücktaste = Select · Leertaste = Schnellvorlauf · " +
        "P = Pause · F5/F9 = Schnellspeichern/-laden · F11 = Vollbild · F4 = Bildeffekt · DS: F2 = Anordnung, F3 = tauschen, " +
        "Touch per Maus/Finger oder rechtem Stick · Beim Schließen wird automatisch gesichert (Belegung änderbar im Tab Steuerung)";

    private AppSettings _settings;
    private readonly EmulatorSession _session;
    private readonly GamePlatform _platform;
    private WriteableBitmap? _bitmap;
    private long _lastFrameId = -1;
    private bool _closed;
    private DispatcherTimer? _osdTimer;

    /// <summary>Aktuell sichtbare Bildschirme: welcher Ausschnitt des Spielbilds wo im Fenster liegt.</summary>
    private List<(PixelRect Source, Rect Dest)> _screens = [];
    private (PixelRect A, PixelRect B)? _croppedFor;

    /// <summary>Wird ausgelöst, wenn sich Bild-Einstellungen per Hotkey geändert haben.</summary>
    public event Action? VideoSettingsChanged;

    public EmulatorSession Session => _session;

    // Nur für den XAML-Designer der IDE
    public GameWindow() : this(null!, null!) { }

    public GameWindow(EmulatorSession session, AppSettings settings)
    {
        InitializeComponent();
        _session = session;
        _settings = settings;
        if (session is null) return;

        _platform = session.Game.Info.Platform;
        ApplySettings();

        Title = $"{session.Game.Title} – DexHub";
        if (_platform == GamePlatform.NintendoDS)
        {
            session.UseTouchCursor = true;
            Width = 540;
            Height = 800;
        }

        // Tasten abfangen, bevor ein Steuerelement sie bekommt
        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, OnKeyUp, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        Deactivated += (_, _) =>
        {
            ReleaseAllKeys();
            session.AcceptControllerInput = false; // Controller gehört dem Fenster im Vordergrund
        };
        Activated += (_, _) => session.AcceptControllerInput = true;

        session.Message += msg => Dispatcher.UIThread.Post(() => ShowOsd(msg));
        session.Failed += msg => Dispatcher.UIThread.Post(() => ShowOsd("Fehler: " + msg, persistent: true));
        session.HotkeyPressed += action => Dispatcher.UIThread.Post(() => HandleWindowHotkey(action));

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

    private VideoSettings Video => _settings.VideoFor(_platform);

    /// <summary>Neue Einstellungen übernehmen (Spiel-Prozess: settings.json wurde geändert).</summary>
    public void ReplaceSettings(AppSettings fresh)
    {
        // Geänderte Emulator-Optionen an den laufenden Core weitergeben
        var oldOptions = _settings.CoreOptions.GetValueOrDefault(_session.CoreName) ?? [];
        foreach (var (key, value) in fresh.CoreOptions.GetValueOrDefault(_session.CoreName) ?? [])
            if (oldOptions.GetValueOrDefault(key) != value) _session.RequestOptionChange(key, value);

        _settings = fresh;
        Input.SdlGamepad.Instance.Settings = fresh.Input;
        ApplySettings();
    }

    /// <summary>Übernimmt Lautstärke, Schnellvorlauf, Bildglättung – auch während das Spiel läuft.</summary>
    public void ApplySettings()
    {
        _session.Volume = _settings.VolumeGain;
        _session.FastForwardSpeed = _settings.FastForwardSpeed;
        _session.FastForwardMode = _settings.FastForwardMode;

        var mode = Video.Smooth ? BitmapInterpolationMode.HighQuality : BitmapInterpolationMode.None;
        RenderOptions.SetBitmapInterpolationMode(ScreenA, mode);
        RenderOptions.SetBitmapInterpolationMode(ScreenB, mode);
        _croppedFor = null; // Ausschnitte neu aufbauen
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
                _lastFrameId = -1;
                _croppedFor = null;
            }

            bool updated;
            using (var fb = _bitmap.Lock())
                updated = _session.CopyFrame(ref _lastFrameId, fb.Address, fb.RowBytes, w, h);

            ArrangeScreens(w, h);
            if (updated)
            {
                ScreenA.InvalidateVisual();
                ScreenB.InvalidateVisual();
            }
            UpdateTouchCursor(w, h);
        }

        RequestAnimationFrame(OnAnimationFrame);
    }

    /// <summary>DS-Spiele liefern beide Bildschirme übereinander (z. B. 256×384). Dann teilen wir auf.</summary>
    private bool IsSplitDs(int w, int h) => _platform == GamePlatform.NintendoDS && h * 2 == w * 3;

    /// <summary>Berechnet, welcher Teil des Spielbilds wo und wie groß im Fenster erscheint.</summary>
    private void ArrangeScreens(int w, int h)
    {
        var video = Video;

        // 1. Teile in "Spielpixel-Koordinaten" anordnen
        var parts = new List<(PixelRect Source, Rect Layout)>();
        if (!IsSplitDs(w, h))
        {
            parts.Add((new PixelRect(0, 0, w, h), new Rect(0, 0, w, h)));
        }
        else
        {
            int sh = h / 2;
            var top = new PixelRect(0, 0, w, sh);
            var bottom = new PixelRect(0, sh, w, sh);
            var (main, second) = video.DsSwapScreens ? (bottom, top) : (top, bottom);

            switch (video.DsLayout)
            {
                case DsLayout.SideBySide:
                    parts.Add((main, new Rect(0, 0, w, sh)));
                    parts.Add((second, new Rect(w, 0, w, sh)));
                    break;
                case DsLayout.Hybrid:
                    parts.Add((main, new Rect(0, 0, w, sh)));
                    parts.Add((second, new Rect(w, sh / 2.0, w / 2.0, sh / 2.0)));
                    break;
                case DsLayout.MainOnly:
                    parts.Add((main, new Rect(0, 0, w, sh)));
                    break;
                case DsLayout.SecondOnly:
                    parts.Add((second, new Rect(0, 0, w, sh)));
                    break;
                default: // übereinander
                    parts.Add((main, new Rect(0, 0, w, sh)));
                    parts.Add((second, new Rect(0, sh, w, sh)));
                    break;
            }
        }

        // 2. Gesamtgröße und Maßstab fürs Fenster
        double contentW = parts.Max(p => p.Layout.Right);
        double contentH = parts.Max(p => p.Layout.Bottom);
        var area = Surface.Bounds.Size;
        if (area.Width <= 0 || area.Height <= 0) return;

        double sx = area.Width / contentW, sy = area.Height / contentH;
        switch (video.Scaling)
        {
            case ScalingMode.Integer:
                double s = Math.Min(sx, sy);
                sx = sy = s >= 1 ? Math.Floor(s) : s;
                break;
            case ScalingMode.Fit:
                sx = sy = Math.Min(sx, sy);
                break;
            // Stretch: sx und sy bleiben unterschiedlich
        }
        double offsetX = (area.Width - contentW * sx) / 2;
        double offsetY = (area.Height - contentH * sy) / 2;

        var screens = parts
            .Select(p => (Source: p.Source, Dest: new Rect(offsetX + p.Layout.X * sx, offsetY + p.Layout.Y * sy,
                                                           p.Layout.Width * sx, p.Layout.Height * sy)))
            .ToList();

        // 3. Bilder setzen (Ausschnitte nur neu anlegen, wenn sich etwas geändert hat)
        var a = screens[0].Source;
        var b = screens.Count > 1 ? screens[1].Source : default;
        if (_bitmap is not null && _croppedFor != (a, b))
        {
            bool full = a.Width == w && a.Height == h;
            ScreenA.Source = full ? (IImage)_bitmap : new CroppedBitmap(_bitmap, a);
            ScreenB.Source = screens.Count > 1 ? new CroppedBitmap(_bitmap, b) : null;
            _croppedFor = (a, b);
        }

        Place(ScreenA, screens[0].Dest);
        ScreenB.IsVisible = screens.Count > 1;
        if (screens.Count > 1) Place(ScreenB, screens[1].Dest);

        _screens = screens;
        Effects.Update(video.Effect, screens);
    }

    private static void Place(Control control, Rect rect)
    {
        Canvas.SetLeft(control, rect.X);
        Canvas.SetTop(control, rect.Y);
        control.Width = rect.Width;
        control.Height = rect.Height;
    }

    /// <summary>Spielbild-Koordinate (0–1 übers ganze Bild) -> Punkt im Fenster, falls sichtbar.</summary>
    private Point? FrameToWindow(double x, double y, int w, int h)
    {
        double px = x * w, py = y * h;
        foreach (var (src, dest) in _screens)
        {
            if (px < src.X || px > src.Right || py < src.Y || py > src.Bottom) continue;
            return new Point(dest.X + (px - src.X) / src.Width * dest.Width,
                             dest.Y + (py - src.Y) / src.Height * dest.Height);
        }
        return null;
    }

    /// <summary>Punkt im Fenster -> Spielbild-Koordinate (0–1), falls er auf einem Bildschirm liegt.</summary>
    private (double X, double Y)? WindowToFrame(Point p, int w, int h)
    {
        foreach (var (src, dest) in _screens)
        {
            if (!dest.Contains(p)) continue;
            double fx = (p.X - dest.X) / dest.Width, fy = (p.Y - dest.Y) / dest.Height;
            return ((src.X + fx * src.Width) / w, (src.Y + fy * src.Height) / h);
        }
        return null;
    }

    /// <summary>Zeichnet den Controller-Zeiger an die passende Stelle über dem Spielbild.</summary>
    private void UpdateTouchCursor(int w, int h)
    {
        var (x, y, visible, pressed) = _session.TouchCursor;
        var point = visible ? FrameToWindow(x, y, w, h) : null;
        TouchCursor.IsVisible = point is not null;
        if (point is not { } p) return;

        Canvas.SetLeft(TouchCursor, p.X - TouchCursor.Width / 2);
        Canvas.SetTop(TouchCursor, p.Y - TouchCursor.Height / 2);
        TouchCursor.Opacity = pressed ? 1.0 : 0.75;
    }

    // =====================================================================
    // Hotkeys für Bild-Einstellungen
    // =====================================================================

    private void HandleWindowHotkey(HotkeyAction action)
    {
        var video = Video;
        switch (action)
        {
            case HotkeyAction.NextDsLayout when _platform == GamePlatform.NintendoDS:
                video.DsLayout = (DsLayout)(((int)video.DsLayout + 1) % Enum.GetValues<DsLayout>().Length);
                ShowOsd("Anordnung: " + VideoSettings.DsLayoutLabels[(int)video.DsLayout]);
                break;
            case HotkeyAction.SwapDsScreens when _platform == GamePlatform.NintendoDS:
                video.DsSwapScreens = !video.DsSwapScreens;
                ShowOsd(video.DsSwapScreens ? "Bildschirme getauscht" : "Bildschirme normal");
                break;
            case HotkeyAction.NextEffect:
                video.Effect = (OverlayEffect)(((int)video.Effect + 1) % Enum.GetValues<OverlayEffect>().Length);
                ShowOsd("Bildeffekt: " + VideoSettings.EffectLabels[(int)video.Effect]);
                break;
            default:
                return;
        }
        _settings.Save();
        _croppedFor = null;
        VideoSettingsChanged?.Invoke();
    }

    // =====================================================================
    // Eingabe
    // =====================================================================

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
                if (IsHotkey(e.Key, HotkeyAction.SaveState)) { _session.RequestSaveState(); handled = true; }
                if (IsHotkey(e.Key, HotkeyAction.LoadState)) { _session.RequestLoadState(); handled = true; }
                foreach (var action in new[] { HotkeyAction.NextDsLayout, HotkeyAction.SwapDsScreens, HotkeyAction.NextEffect })
                    if (IsHotkey(e.Key, action)) { HandleWindowHotkey(action); handled = true; }
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
        UpdatePointer(e, e.GetCurrentPoint(Surface).Properties.IsLeftButtonPressed);
    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e) => UpdatePointer(e, false);

    /// <summary>Rechnet die Maus-/Fingerposition in Koordinaten auf dem Spielbild um (DS-Touchscreen).</summary>
    private void UpdatePointer(PointerEventArgs e, bool pressed)
    {
        var (w, h) = _session.FrameSize;
        if (w == 0 || h == 0) return;

        var frame = WindowToFrame(e.GetPosition(Surface), w, h);
        if (frame is { } f) _session.SetPointer(f.X, f.Y, pressed);
        else if (!pressed) _session.SetPointer(0, 0, false);
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
