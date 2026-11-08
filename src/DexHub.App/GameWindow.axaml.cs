using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using DexHub.Core.Emulation;
using DexHub.Core.Libretro;
using GamePlatform = DexHub.Core.Models.Platform;

namespace DexHub.App;

/// <summary>Fenster, in dem ein Spiel läuft.</summary>
public partial class GameWindow : Window
{
    /// <summary>
    /// Tastaturbelegung. Y und Z sind beide B, damit es auf deutscher und englischer Tastatur passt.
    /// </summary>
    private static readonly Dictionary<Key, JoypadButton> KeyMap = new()
    {
        [Key.Up] = JoypadButton.Up,
        [Key.Down] = JoypadButton.Down,
        [Key.Left] = JoypadButton.Left,
        [Key.Right] = JoypadButton.Right,
        [Key.X] = JoypadButton.A,
        [Key.Y] = JoypadButton.B,
        [Key.Z] = JoypadButton.B,
        [Key.S] = JoypadButton.X,
        [Key.A] = JoypadButton.Y,
        [Key.Q] = JoypadButton.L,
        [Key.W] = JoypadButton.R,
        [Key.Enter] = JoypadButton.Start,
        [Key.Back] = JoypadButton.Select,
        [Key.RightShift] = JoypadButton.Select,
    };

    public const string ControlsHelp =
        "Steuerung: Pfeiltasten · X = A · Y/Z = B · A = Y · S = X · Q/W = L/R · " +
        "Enter = Start · Rücktaste = Select · P = Pause · F5 = Schnellspeichern · F9 = Laden";

    private readonly EmulatorSession _session;
    private WriteableBitmap? _bitmap;
    private long _lastFrameId = -1;
    private bool _closed;
    private DispatcherTimer? _osdTimer;

    // Nur für den XAML-Designer der IDE
    public GameWindow() : this(null!) { }

    public GameWindow(EmulatorSession session)
    {
        InitializeComponent();
        _session = session;
        if (session is null) return;

        Title = $"{session.Game.Title} – DexHub";
        if (session.Game.Info.Platform == GamePlatform.NintendoDS)
        {
            // Zwei Bildschirme übereinander
            Width = 540;
            Height = 800;
        }

        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;

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
        }

        RequestAnimationFrame(OnAnimationFrame);
    }

    // =====================================================================
    // Eingabe
    // =====================================================================

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
            case Key.P:
                _session.TogglePause();
                ShowOsd(_session.IsPaused ? "Pausiert (P zum Fortsetzen)" : "Weiter", persistent: _session.IsPaused);
                break;
            default:
                if (KeyMap.TryGetValue(e.Key, out var button)) _session.SetButton(button, true);
                else return;
                break;
        }
        e.Handled = true;
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (KeyMap.TryGetValue(e.Key, out var button))
        {
            _session.SetButton(button, false);
            e.Handled = true;
        }
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
