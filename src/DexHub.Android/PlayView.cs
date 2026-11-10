using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using DexHub.Core.Emulation;
using DexHub.Core.Libretro;
using DexHub.Core.Models;
using DexHub.Core.Settings;

namespace DexHub.Mobile;

/// <summary>
/// Das laufende Spiel: Bild oben (beim DS beide Bildschirme, der untere ist berührbar),
/// darunter die Tasten. Im Querformat liegen die Tasten halb durchsichtig über dem Bild.
/// </summary>
public sealed class PlayView : UserControl
{
    public event Action? Closed;

    private readonly EmulatorSession _session;
    private readonly GameEntry _game;
    private readonly Canvas _surface = new() { ClipToBounds = true, Background = Brushes.Black };
    private readonly Image _screenA = new() { Stretch = Stretch.Fill };
    private readonly Image _screenB = new() { Stretch = Stretch.Fill };
    private readonly Grid _layout = new() { RowDefinitions = new RowDefinitions("*,Auto") };
    private readonly Control _controls;
    private readonly TextBlock _osd = MobileView.Text("", 14);
    private readonly Border _osdBox;
    private WriteableBitmap? _bitmap;
    private long _lastFrameId = -1;
    private (PixelRect, PixelRect)? _croppedFor;
    private bool _closing, _fastForward, _controlsHidden;
    private DispatcherTimer? _osdTimer;

    private bool IsDs => _game.Info.Platform == Platform.NintendoDS;

    public PlayView(GameEntry game, string corePath, bool resume, AppSettings settings)
    {
        _game = game;
        _session = new EmulatorSession(game, corePath, new AndroidAudioSink())
        {
            InputProvider = AndroidGamepad.Instance,
            AutoResume = resume && settings.AutoResume,
            Volume = settings.VolumeGain,
            FastForwardSpeed = settings.FastForwardSpeed,
            FastForwardMode = FastForwardMode.Hold,
            CoreOptions = new Dictionary<string, string>(settings.CoreOptions.GetValueOrDefault(Path.GetFileNameWithoutExtension(corePath)) ?? []),
        };
        _session.Message += m => Dispatcher.UIThread.Post(() => ShowOsd(m));
        _session.Failed += m => Dispatcher.UIThread.Post(() => ShowOsd("Fehler: " + m));
        _session.QuitRequested += () => Dispatcher.UIThread.Post(RequestClose);

        RenderOptions.SetBitmapInterpolationMode(_screenA, BitmapInterpolationMode.None);
        RenderOptions.SetBitmapInterpolationMode(_screenB, BitmapInterpolationMode.None);
        _surface.Children.Add(_screenA);
        _surface.Children.Add(_screenB);
        HookTouchScreen();

        _controls = BuildControls();
        var osdBox = _osdBox = new Border
        {
            IsVisible = false,
            Background = new SolidColorBrush(Color.Parse("#B0000000")),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4),
            Margin = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Child = _osd,
        };

        _layout.Children.Add(_surface);
        _layout.Children.Add(osdBox);
        Grid.SetRow(_controls, 1);
        _layout.Children.Add(_controls);
        Content = _layout;
        Background = Brushes.Black;

        SizeChanged += (_, _) => UpdateOrientation();
        AttachedToVisualTree += (_, _) =>
        {
            AndroidGamepad.Reset();
            AndroidGamepad.Active = true;
            _session.Start();
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnAnimationFrame);
        };
    }

    // ---------- Beenden / Pausieren ----------

    public void RequestClose()
    {
        if (_closing) return;
        _closing = true;
        AndroidGamepad.Active = false;
        ShowOsd("Speichere …");
        Task.Run(() =>
        {
            _session.Dispose(); // hält den Emulator an, speichert Spielstand und Fortsetzen-Punkt
            Dispatcher.UIThread.Post(() => Closed?.Invoke());
        });
    }

    /// <summary>App im Hintergrund: anhalten (und danach wieder weiter, wenn nicht schon vorher pausiert).</summary>
    private bool _pausedByBackground;
    public void SetBackground(bool background)
    {
        if (_closing) return;
        if (background && !_session.IsPaused)
        {
            _session.TogglePause();
            _pausedByBackground = true;
        }
        else if (!background && _pausedByBackground)
        {
            _session.TogglePause();
            _pausedByBackground = false;
        }
    }

    // ---------- Bild ----------

    private void OnAnimationFrame(TimeSpan _)
    {
        if (_closing) return;
        var (w, h) = _session.FrameSize;
        if (w > 0 && h > 0)
        {
            if (_bitmap is null || _bitmap.PixelSize.Width != w || _bitmap.PixelSize.Height != h)
            {
                _bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                _lastFrameId = -1;
                _croppedFor = null;
            }
            bool updated;
            using (var fb = _bitmap.Lock())
                updated = _session.CopyFrame(ref _lastFrameId, fb.Address, fb.RowBytes, w, h);
            Arrange(w, h);
            if (updated)
            {
                _screenA.InvalidateVisual();
                _screenB.InvalidateVisual();
            }
        }
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnAnimationFrame);
    }

    private bool IsSplit(int w, int h) => IsDs && h * 2 == w * 3;

    /// <summary>Bild(er) möglichst groß und mit gleichem Seitenverhältnis in die Fläche legen.</summary>
    private void Arrange(int w, int h)
    {
        var area = _surface.Bounds.Size;
        if (area.Width <= 0 || area.Height <= 0 || _bitmap is null) return;

        bool split = IsSplit(w, h);
        bool landscape = area.Width > area.Height;
        int sh = split ? h / 2 : h;

        // DS im Querformat: nebeneinander, sonst übereinander
        double contentW = split && landscape ? w * 2 : w;
        double contentH = split && !landscape ? h : sh;
        double scale = Math.Min(area.Width / contentW, area.Height / contentH);
        if (scale >= 1) scale = Math.Floor(scale * 4) / 4; // leicht gerundet, wirkt schärfer
        double ox = (area.Width - contentW * scale) / 2, oy = (area.Height - contentH * scale) / 2;

        var top = new PixelRect(0, 0, w, sh);
        var bottom = split ? new PixelRect(0, sh, w, sh) : default;
        if (_croppedFor != (top, bottom))
        {
            _screenA.Source = split ? new CroppedBitmap(_bitmap, top) : _bitmap;
            _screenB.Source = split ? new CroppedBitmap(_bitmap, bottom) : null;
            _croppedFor = (top, bottom);
        }

        Place(_screenA, new Rect(ox, oy, w * scale, sh * scale));
        _screenB.IsVisible = split;
        if (split)
            Place(_screenB, landscape
                ? new Rect(ox + w * scale, oy, w * scale, sh * scale)
                : new Rect(ox, oy + sh * scale, w * scale, sh * scale));
    }

    private static void Place(Control c, Rect r)
    {
        Canvas.SetLeft(c, r.X);
        Canvas.SetTop(c, r.Y);
        c.Width = r.Width;
        c.Height = r.Height;
    }

    /// <summary>DS: Tippen auf den unteren Bildschirm = Touchscreen.</summary>
    private void HookTouchScreen()
    {
        void Send(PointerEventArgs e, bool pressed)
        {
            if (!_screenB.IsVisible || _screenB.Bounds.Width <= 0) return;
            var p = e.GetPosition(_screenB);
            double u = Math.Clamp(p.X / _screenB.Bounds.Width, 0, 1);
            double v = Math.Clamp(p.Y / _screenB.Bounds.Height, 0, 1);
            _session.SetPointer(u, 0.5 + v / 2, pressed); // unterer Bildschirm = untere Hälfte des Spielbilds
        }
        _screenB.PointerPressed += (_, e) => { e.Pointer.Capture(_screenB); Send(e, true); e.Handled = true; };
        _screenB.PointerMoved += (_, e) => { if (e.Pointer.Captured == _screenB) Send(e, true); };
        _screenB.PointerReleased += (_, e) => { Send(e, false); e.Pointer.Capture(null); };
        _screenB.PointerCaptureLost += (_, _) => _session.SetPointer(0, 0, false);
    }

    private void UpdateOrientation()
    {
        bool landscape = Bounds.Width > Bounds.Height;
        // Querformat: Tasten liegen über dem Bild (halb durchsichtig), Hochformat: darunter
        Grid.SetRow(_controls, landscape ? 0 : 1);
        _controls.Opacity = landscape ? 0.55 : 1;
        _controls.VerticalAlignment = landscape ? VerticalAlignment.Stretch : VerticalAlignment.Bottom;
        _controls.IsVisible = !_controlsHidden;
    }

    // ---------- Tasten ----------

    private Control BuildControls()
    {
        var dpad = new DPad(_session);

        var face = new Grid
        {
            Width = 170, Height = 170,
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            RowDefinitions = new RowDefinitions("*,*,*"),
        };
        void Face(string label, JoypadButton button, int row, int col)
        {
            var b = new PadButton(label, () => _session.SetButton(button, true), () => _session.SetButton(button, false), round: true);
            Grid.SetRow(b, row);
            Grid.SetColumn(b, col);
            face.Children.Add(b);
        }
        Face("A", JoypadButton.A, 1, 2);
        Face("B", JoypadButton.B, 2, 1);
        if (IsDs)
        {
            Face("X", JoypadButton.X, 0, 1);
            Face("Y", JoypadButton.Y, 1, 0);
        }

        PadButton Small(string label, JoypadButton button) =>
            new(label, () => _session.SetButton(button, true), () => _session.SetButton(button, false)) { Width = 80, Height = 38 };

        var ff = new PadButton("⏩", () =>
        {
            _fastForward = !_fastForward;
            _session.SetFastForward(_fastForward);
            ShowOsd(_fastForward ? "Vorspulen an" : "Vorspulen aus");
        }, null) { Width = 56, Height = 38 };

        var menuButton = new Button { Content = "☰", Width = 56, Height = 38, HorizontalContentAlignment = HorizontalAlignment.Center };
        menuButton.Flyout = BuildMenu();

        bool shoulders = _game.Info.Platform is Platform.GameBoyAdvance or Platform.NintendoDS;
        var topRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,*,Auto"), Margin = new Thickness(8, 4) };
        if (shoulders) topRow.Children.Add(Small("L", JoypadButton.L));
        var ffHolder = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { ff, menuButton } };
        Grid.SetColumn(ffHolder, 2);
        topRow.Children.Add(ffHolder);
        if (shoulders)
        {
            var r = Small("R", JoypadButton.R);
            Grid.SetColumn(r, 5);
            topRow.Children.Add(r);
        }

        var middle = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12),
            Children = { Small("Select", JoypadButton.Select), Small("Start", JoypadButton.Start) },
        };

        var main = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(12, 0, 12, 16) };
        main.Children.Add(dpad);
        Grid.SetColumn(middle, 1);
        main.Children.Add(middle);
        Grid.SetColumn(face, 2);
        main.Children.Add(face);

        var panel = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(topRow, Dock.Top);
        DockPanel.SetDock(main, Dock.Bottom);
        panel.Children.Add(topRow);
        panel.Children.Add(main);
        return panel;
    }

    private Flyout BuildMenu()
    {
        var stack = new StackPanel { Spacing = 6, Width = 240 };
        Button Item(string text, Action action)
        {
            var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch };
            b.Click += (_, _) => action();
            return b;
        }
        var flyout = new Flyout { Content = stack };
        stack.Children.Add(Item("💾 Schnell speichern", () => { _session.RequestSaveState(); flyout.Hide(); }));
        stack.Children.Add(Item("📂 Schnell laden", () => { _session.RequestLoadState(); flyout.Hide(); }));
        stack.Children.Add(Item("🎮 Bildschirmtasten ein/aus", () =>
        {
            _controlsHidden = !_controlsHidden;
            UpdateOrientation();
            flyout.Hide();
        }));
        stack.Children.Add(Item("⏸ Pause", () => { _session.TogglePause(); ShowOsd(_session.IsPaused ? "Pause" : "Weiter"); flyout.Hide(); }));
        stack.Children.Add(Item("✕ Spiel beenden", () => { flyout.Hide(); RequestClose(); }));
        return flyout;
    }

    private void ShowOsd(string text)
    {
        _osd.Text = text;
        _osdBox.IsVisible = true;
        _osdTimer?.Stop();
        _osdTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _osdTimer.Tick += (_, _) => { _osdBox.IsVisible = false; _osdTimer?.Stop(); };
        _osdTimer.Start();
    }
}

/// <summary>Bildschirm-Taste: reagiert auf Drücken und Loslassen (mehrere Finger gleichzeitig möglich).</summary>
internal sealed class PadButton : Border
{
    private static readonly IBrush Normal = new SolidColorBrush(Color.Parse("#55FFFFFF"));
    private static readonly IBrush Pressed = new SolidColorBrush(Color.Parse("#99FFFFFF"));
    private readonly Action _down;
    private readonly Action? _up;
    private bool _isDown;

    public PadButton(string label, Action down, Action? up, bool round = false)
    {
        _down = down;
        _up = up;
        Background = Normal;
        CornerRadius = new CornerRadius(round ? 30 : 8);
        Margin = new Thickness(3);
        MinWidth = 50;
        MinHeight = 50;
        Child = new TextBlock
        {
            Text = label, FontSize = 18, FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        PointerPressed += (_, e) =>
        {
            e.Pointer.Capture(this);
            e.Handled = true;
            Press(true);
        };
        PointerReleased += (_, e) => { Press(false); e.Pointer.Capture(null); };
        PointerCaptureLost += (_, _) => Press(false);
    }

    private void Press(bool down)
    {
        if (down == _isDown) return;
        _isDown = down;
        Background = down ? Pressed : Normal;
        if (down) _down();
        else _up?.Invoke();
    }
}

/// <summary>Steuerkreuz als eine Fläche: Richtung (auch schräg) aus der Fingerposition, Wischen wechselt die Richtung.</summary>
internal sealed class DPad : Border
{
    private readonly EmulatorSession _session;
    private int _current;

    public DPad(EmulatorSession session)
    {
        _session = session;
        Width = Height = 160;
        CornerRadius = new CornerRadius(80);
        Background = new SolidColorBrush(Color.Parse("#33FFFFFF"));
        Child = new TextBlock
        {
            Text = "✚", FontSize = 90, Opacity = 0.5,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        PointerPressed += (_, e) => { e.Pointer.Capture(this); e.Handled = true; Update(e.GetPosition(this)); };
        PointerMoved += (_, e) => { if (e.Pointer.Captured == this) Update(e.GetPosition(this)); };
        PointerReleased += (_, e) => { Set(0); e.Pointer.Capture(null); };
        PointerCaptureLost += (_, _) => Set(0);
    }

    private void Update(Point p)
    {
        double dx = p.X - Bounds.Width / 2, dy = p.Y - Bounds.Height / 2;
        double dead = Bounds.Width * 0.12;
        int bits = 0;
        // Schräg zählt, wenn beide Achsen deutlich ausgelenkt sind
        if (Math.Abs(dx) > dead && Math.Abs(dx) > Math.Abs(dy) * 0.45)
            bits |= 1 << (int)(dx < 0 ? JoypadButton.Left : JoypadButton.Right);
        if (Math.Abs(dy) > dead && Math.Abs(dy) > Math.Abs(dx) * 0.45)
            bits |= 1 << (int)(dy < 0 ? JoypadButton.Up : JoypadButton.Down);
        Set(bits);
    }

    private void Set(int bits)
    {
        if (bits == _current) return;
        foreach (var b in new[] { JoypadButton.Up, JoypadButton.Down, JoypadButton.Left, JoypadButton.Right })
        {
            int bit = 1 << (int)b;
            if ((bits & bit) != (_current & bit)) _session.SetButton(b, (bits & bit) != 0);
        }
        _current = bits;
    }
}
