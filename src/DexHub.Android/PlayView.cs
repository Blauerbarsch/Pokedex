using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
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
/// Das laufende Spiel auf Android, im Pokédex-Look: oben eine schmale Leiste (Beenden, Vorspulen, Speichern,
/// Menü), in der Mitte das Bild (beim DS beide Bildschirme, der untere ist berührbar), unten die Tasten.
/// Im Querformat liegen Steuerkreuz und Knöpfe halb durchsichtig links und rechts neben/über dem Bild.
/// Sobald ein Controller benutzt wird, verschwinden die Bildschirmtasten (☰ blendet sie wieder ein).
/// </summary>
public sealed class PlayView : UserControl
{
    public event Action? Closed;

    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#DC0A2D"));
    private static readonly IBrush RedPressed = new SolidColorBrush(Color.Parse("#FF4D63"));
    private static readonly IBrush Dark = new SolidColorBrush(Color.Parse("#2A2E38"));
    private static readonly IBrush DarkPressed = new SolidColorBrush(Color.Parse("#454C5A"));
    private static readonly IBrush Body = new SolidColorBrush(Color.Parse("#1A1D23"));

    private readonly EmulatorSession _session;
    private readonly GameEntry _game;
    private readonly Canvas _surface = new() { ClipToBounds = true, Background = Brushes.Black };
    private readonly Image _screenA = new() { Stretch = Stretch.Fill };
    private readonly Image _screenB = new() { Stretch = Stretch.Fill };
    private readonly Grid _root = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
    private readonly Border _controlsPortrait;
    private readonly Grid _controlsLandscape;
    private readonly TextBlock _osd = new() { FontSize = 14, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
    private readonly Border _osdBox;
    private readonly BarToggle _ffButton;
    private WriteableBitmap? _bitmap;
    private long _lastFrameId = -1;
    private (PixelRect, PixelRect)? _croppedFor;
    private bool _closing, _controlsHidden, _pausedByBackground, _landscape;
    private DispatcherTimer? _osdTimer;

    private bool IsDs => _game.Info.Platform == Platform.NintendoDS;
    private bool HasShoulders => _game.Info.Platform is Platform.GameBoyAdvance or Platform.NintendoDS;

    public PlayView(GameEntry game, string corePath, bool resume, AppSettings settings, Dictionary<string, string>? options)
    {
        _game = game;
        AndroidGamepad.Settings = settings.Input;

        var coreOptions = new Dictionary<string, string>(settings.CoreOptions.GetValueOrDefault(System.IO.Path.GetFileNameWithoutExtension(corePath)) ?? []);
        if (options is not null) foreach (var (k, v) in options) coreOptions[k] = v;

        _session = new EmulatorSession(game, corePath, new AndroidAudioSink())
        {
            InputProvider = AndroidGamepad.Instance,
            AutoResume = resume,
            Volume = settings.VolumeGain,
            FastForwardSpeed = settings.FastForwardSpeed,
            FastForwardMode = FastForwardMode.Hold,
            CoreOptions = coreOptions,
        };
        _session.Message += m => Dispatcher.UIThread.Post(() => ShowOsd(m));
        _session.Failed += m => Dispatcher.UIThread.Post(() => ShowOsd("Fehler: " + m, seconds: 8));
        _session.QuitRequested += () => Dispatcher.UIThread.Post(RequestClose);
        AndroidGamepad.Used += OnGamepadUsed;

        RenderOptions.SetBitmapInterpolationMode(_screenA, BitmapInterpolationMode.None);
        RenderOptions.SetBitmapInterpolationMode(_screenB, BitmapInterpolationMode.None);
        _surface.Children.Add(_screenA);
        _surface.Children.Add(_screenB);
        HookTouchScreen();

        _ffButton = new BarToggle("»", on =>
        {
            _session.SetFastForward(on);
            ShowOsd(on ? "Vorspulen an" : "Vorspulen aus");
        });

        _osdBox = new Border
        {
            IsVisible = false,
            Background = new SolidColorBrush(Color.Parse("#E01A1D23")),
            BorderBrush = Red,
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 7),
            Margin = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Child = _osd,
        };

        _controlsPortrait = BuildPortraitControls();
        _controlsLandscape = BuildLandscapeControls();

        var topBar = BuildTopBar();
        _root.Children.Add(topBar);
        Grid.SetRow(_surface, 1);
        _root.Children.Add(_surface);
        Grid.SetRow(_controlsLandscape, 1);
        _root.Children.Add(_controlsLandscape);
        Grid.SetRow(_osdBox, 1);
        _root.Children.Add(_osdBox);
        Grid.SetRow(_controlsPortrait, 2);
        _root.Children.Add(_controlsPortrait);

        Content = _root;
        Background = new SolidColorBrush(Color.Parse("#15171C"));

        SizeChanged += (_, _) => UpdateOrientation();
        AttachedToVisualTree += (_, _) =>
        {
            AndroidGamepad.Reset();
            AndroidGamepad.Active = true;
            _session.Start();
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnAnimationFrame);
        };
    }

    // =====================================================================
    // Beenden / Pausieren
    // =====================================================================

    public void RequestClose()
    {
        if (_closing) return;
        _closing = true;
        AndroidGamepad.Active = false;
        AndroidGamepad.Used -= OnGamepadUsed;
        ShowOsd("Speichere …");
        Task.Run(() =>
        {
            try { _session.Dispose(); } // hält den Emulator an, speichert Spielstand und Fortsetzen-Punkt
            catch (Exception ex) { Console.WriteLine("[DexHub] Beenden: " + ex); }
            Dispatcher.UIThread.Post(() => Closed?.Invoke());
        });
    }

    /// <summary>App im Hintergrund: anhalten (und danach wieder weiter, wenn nicht schon vorher pausiert).</summary>
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

    private void OnGamepadUsed()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_controlsHidden || _closing) return;
            _controlsHidden = true;
            UpdateOrientation();
            ShowOsd("Controller erkannt – Bildschirmtasten ausgeblendet (☰ blendet sie wieder ein)");
        });
    }

    // =====================================================================
    // Bild
    // =====================================================================

    private int _traceFrames;

    private void OnAnimationFrame(TimeSpan _)
    {
        if (_closing) return;
        var (w, h) = _session.FrameSize;
        if (DexHub.Core.Libretro.LibretroCore.Trace && _traceFrames++ % 120 == 0)
            Console.WriteLine($"[DexHub] Anzeige: Bild {w}x{h}, Fläche {_surface.Bounds.Size}, Nr. {_lastFrameId}, Bildquelle {_screenA.Source is not null}, Größe {_screenA.Bounds.Size}");
        if (w > 0 && h > 0)
        {
            if (_bitmap is null || _bitmap.PixelSize.Width != w || _bitmap.PixelSize.Height != h)
            {
                _bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Opaque);
                _lastFrameId = -1;
                _croppedFor = null;
            }
            bool updated;
            using (var fb = _bitmap.Lock())
            {
                updated = _session.CopyFrame(ref _lastFrameId, fb.Address, fb.RowBytes, w, h, rgba: true);
                if (updated && DexHub.Core.Libretro.LibretroCore.Trace && _traceFrames % 120 == 1)
                    unsafe
                    {
                        var px = (uint*)fb.Address;
                        uint sum = 0;
                        for (int i = 0; i < w * h; i += 97) sum += px[i] & 0xFFFFFF;
                        Console.WriteLine($"[DexHub] Pixel: Mitte 0x{px[(h / 2) * (fb.RowBytes / 4) + w / 2]:X8}, Summe {sum}, Format {fb.Format}, Zeile {fb.RowBytes}");
                    }
            }
            ArrangeScreens(w, h);
            if (updated)
            {
                _screenA.InvalidateVisual();
                _screenB.InvalidateVisual();
            }
        }
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnAnimationFrame);
    }

    private bool IsSplit(int w, int h) => IsDs && h * 2 == w * 3;

    /// <summary>Bild(er) möglichst groß mit gleichem Seitenverhältnis in die Fläche legen.</summary>
    private void ArrangeScreens(int w, int h)
    {
        var area = _surface.Bounds.Size;
        if (area.Width <= 0 || area.Height <= 0 || _bitmap is null) return;

        bool split = IsSplit(w, h);
        int sh = split ? h / 2 : h;
        // DS im Querformat nebeneinander, sonst übereinander
        double contentW = split && _landscape ? w * 2 : w;
        double contentH = split && !_landscape ? h : sh;
        double scale = Math.Min(area.Width / contentW, area.Height / contentH);
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
            Place(_screenB, _landscape
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
        _landscape = Bounds.Width > Bounds.Height;
        _controlsPortrait.IsVisible = !_landscape && !_controlsHidden;
        _controlsLandscape.IsVisible = _landscape && !_controlsHidden;
        _croppedFor = null; // DS-Bildschirme neu anordnen
    }

    // =====================================================================
    // Bedienelemente
    // =====================================================================

    private Control BuildTopBar()
    {
        var quit = new Button { Content = "✕ Beenden", Classes = { "flat" }, Foreground = Brushes.White };
        quit.Click += (_, _) => RequestClose();
        var save = new Button { Content = "💾", Classes = { "flat" }, Foreground = Brushes.White };
        save.Click += (_, _) => _session.RequestSaveState();
        var load = new Button { Content = "📂", Classes = { "flat" }, Foreground = Brushes.White };
        load.Click += (_, _) => _session.RequestLoadState();
        var menu = new Button { Content = "☰", Classes = { "flat" }, Foreground = Brushes.White, Flyout = BuildMenu() };

        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto,Auto"), Margin = new Thickness(4, 2) };
        bar.Children.Add(quit);
        var title = new TextBlock
        {
            Text = _game.Title, Foreground = Brushes.White, FontWeight = FontWeight.SemiBold, FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0),
        };
        Grid.SetColumn(title, 1);
        bar.Children.Add(title);
        Control[] right = [_ffButton, save, load, menu];
        for (int i = 0; i < right.Length; i++)
        {
            Grid.SetColumn(right[i], 2 + i);
            bar.Children.Add(right[i]);
        }
        return new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#D4142F"), 0), new GradientStop(Color.Parse("#A3081F"), 1) },
            },
            Child = bar,
        };
    }

    private Flyout BuildMenu()
    {
        var stack = new StackPanel { Spacing = 6, Width = 250 };
        var flyout = new Flyout { Content = stack };
        Button Item(string text, Action action)
        {
            var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch };
            b.Click += (_, _) => { flyout.Hide(); action(); };
            return b;
        }
        stack.Children.Add(Item("💾 Schnell speichern", _session.RequestSaveState));
        stack.Children.Add(Item("📂 Schnell laden", _session.RequestLoadState));
        stack.Children.Add(Item("🎮 Bildschirmtasten ein/aus", () =>
        {
            _controlsHidden = !_controlsHidden;
            UpdateOrientation();
        }));
        stack.Children.Add(Item("⏸ Pause / Weiter", () =>
        {
            _session.TogglePause();
            ShowOsd(_session.IsPaused ? "Pause" : "Weiter");
        }));
        stack.Children.Add(Item("✕ Spiel beenden", RequestClose));
        return flyout;
    }

    /// <summary>Hochformat: Tasten unten wie bei einem Game Boy.</summary>
    private Border BuildPortraitControls()
    {
        var face = FaceButtons(76);
        var dpad = new DPad(_session, 170);
        var startSelect = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 6),
            Children = { Pill("SELECT", JoypadButton.Select), Pill("START", JoypadButton.Start) },
        };

        var main = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(18, 4, 18, 18) };
        main.Children.Add(dpad);
        Grid.SetColumn(startSelect, 1);
        main.Children.Add(startSelect);
        Grid.SetColumn(face, 2);
        main.Children.Add(face);

        var stack = new StackPanel();
        if (HasShoulders)
        {
            var shoulders = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(12, 10, 12, 4) };
            shoulders.Children.Add(Shoulder("L", JoypadButton.L));
            var r = Shoulder("R", JoypadButton.R);
            Grid.SetColumn(r, 2);
            shoulders.Children.Add(r);
            stack.Children.Add(shoulders);
        }
        stack.Children.Add(main);
        // Für ~400 dp Breite gestaltet; schmalere Handys verkleinern die Tasten gleichmäßig
        stack.Width = 400;

        return new Border
        {
            Background = Body,
            CornerRadius = new CornerRadius(22, 22, 0, 0),
            BorderBrush = new SolidColorBrush(Color.Parse("#2F3440")),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = stack },
        };
    }

    /// <summary>Querformat: Steuerkreuz links, Knöpfe rechts, halb durchsichtig über dem Bild.</summary>
    private Grid BuildLandscapeControls()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Opacity = 0.6, IsVisible = false };

        var left = new StackPanel { Spacing = 14, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(16, 0, 0, 16) };
        if (HasShoulders) left.Children.Add(Shoulder("L", JoypadButton.L));
        left.Children.Add(new DPad(_session, 150));
        left.Children.Add(Pill("SELECT", JoypadButton.Select));

        var right = new StackPanel { Spacing = 14, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 16, 16) };
        if (HasShoulders)
        {
            var r = Shoulder("R", JoypadButton.R);
            r.HorizontalAlignment = HorizontalAlignment.Right;
            right.Children.Add(r);
        }
        right.Children.Add(FaceButtons(66));
        var start = Pill("START", JoypadButton.Start);
        start.HorizontalAlignment = HorizontalAlignment.Right;
        right.Children.Add(start);

        grid.Children.Add(left);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>A/B (rot, schräg versetzt wie beim Game Boy), beim DS zusätzlich X/Y.</summary>
    private Control FaceButtons(double size)
    {
        PadButton Round(string label, JoypadButton button, bool red) =>
            new(label, () => _session.SetButton(button, true), () => _session.SetButton(button, false),
                red ? Red : Dark, red ? RedPressed : DarkPressed)
            { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2) };

        if (IsDs)
        {
            var g = new Grid
            {
                Width = size * 2.6, Height = size * 2.6,
                ColumnDefinitions = new ColumnDefinitions("*,*,*"), RowDefinitions = new RowDefinitions("*,*,*"),
            };
            void Put(Control c, int row, int col) { Grid.SetRow(c, row); Grid.SetColumn(c, col); g.Children.Add(c); }
            Put(Round("X", JoypadButton.X, false), 0, 1);
            Put(Round("Y", JoypadButton.Y, false), 1, 0);
            Put(Round("A", JoypadButton.A, true), 1, 2);
            Put(Round("B", JoypadButton.B, true), 2, 1);
            return g;
        }

        var canvas = new Canvas { Width = size * 2.2, Height = size * 1.8 };
        var b = Round("B", JoypadButton.B, true);
        var a = Round("A", JoypadButton.A, true);
        Canvas.SetLeft(b, 0); Canvas.SetTop(b, size * 0.75);
        Canvas.SetLeft(a, size * 1.15); Canvas.SetTop(a, 0);
        canvas.Children.Add(b);
        canvas.Children.Add(a);
        return canvas;
    }

    private PadButton Pill(string label, JoypadButton button) =>
        new(label, () => _session.SetButton(button, true), () => _session.SetButton(button, false), Dark, DarkPressed, fontSize: 12)
        { Width = 84, Height = 30, CornerRadius = new CornerRadius(15) };

    private PadButton Shoulder(string label, JoypadButton button) =>
        new(label, () => _session.SetButton(button, true), () => _session.SetButton(button, false), Dark, DarkPressed)
        { Width = 110, Height = 40, CornerRadius = new CornerRadius(12) };

    private void ShowOsd(string text, int seconds = 3)
    {
        _osd.Text = text;
        _osdBox.IsVisible = true;
        _osdTimer?.Stop();
        _osdTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _osdTimer.Tick += (_, _) => { _osdBox.IsVisible = false; _osdTimer?.Stop(); };
        _osdTimer.Start();
    }
}

/// <summary>Bildschirm-Taste: reagiert auf Drücken und Loslassen (mehrere Finger gleichzeitig möglich).</summary>
internal sealed class PadButton : Border
{
    private readonly IBrush _normal, _pressed;
    private readonly Action _down;
    private readonly Action? _up;
    private bool _isDown;

    public PadButton(string label, Action down, Action? up, IBrush normal, IBrush pressed, double fontSize = 20)
    {
        _down = down;
        _up = up;
        _normal = normal;
        _pressed = pressed;
        Background = normal;
        BorderBrush = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0));
        BorderThickness = new Thickness(2);
        Margin = new Thickness(3);
        Child = new TextBlock
        {
            Text = label, FontSize = fontSize, FontWeight = FontWeight.Bold, Foreground = Brushes.White,
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
        Background = down ? _pressed : _normal;
        if (down) _down();
        else _up?.Invoke();
    }
}

/// <summary>Ein-/Aus-Knopf in der oberen Leiste.</summary>
internal sealed class BarToggle : Button
{
    private bool _on;

    public BarToggle(string label, Action<bool> changed)
    {
        Content = label;
        Classes.Add("flat");
        Foreground = Brushes.White;
        Click += (_, _) =>
        {
            _on = !_on;
            Background = _on ? new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)) : Brushes.Transparent;
            changed(_on);
        };
    }
}

/// <summary>Steuerkreuz: Richtung (auch schräg) aus der Fingerposition, Wischen wechselt die Richtung.</summary>
internal sealed class DPad : Panel
{
    private readonly EmulatorSession _session;
    private readonly Dictionary<JoypadButton, Border> _arms = [];
    private int _current;
    private static readonly IBrush Arm = new SolidColorBrush(Color.Parse("#2A2E38"));
    private static readonly IBrush ArmPressed = new SolidColorBrush(Color.Parse("#DC0A2D"));

    public DPad(EmulatorSession session, double size)
    {
        _session = session;
        Width = Height = size;
        Background = Brushes.Transparent; // ganze Fläche berührbar
        double t = size / 3;

        Border ArmAt(JoypadButton b, double x, double y, CornerRadius r)
        {
            var arm = new Border { Width = t, Height = t, Background = Arm, CornerRadius = r };
            arm.RenderTransform = null;
            Canvas.SetLeft(arm, x);
            Canvas.SetTop(arm, y);
            _arms[b] = arm;
            return arm;
        }
        var canvas = new Canvas { Width = size, Height = size, IsHitTestVisible = false };
        canvas.Children.Add(ArmAt(JoypadButton.Up, t, 0, new CornerRadius(8, 8, 0, 0)));
        canvas.Children.Add(ArmAt(JoypadButton.Down, t, 2 * t, new CornerRadius(0, 0, 8, 8)));
        canvas.Children.Add(ArmAt(JoypadButton.Left, 0, t, new CornerRadius(8, 0, 0, 8)));
        canvas.Children.Add(ArmAt(JoypadButton.Right, 2 * t, t, new CornerRadius(0, 8, 8, 0)));
        var center = new Border { Width = t, Height = t, Background = Arm };
        Canvas.SetLeft(center, t);
        Canvas.SetTop(center, t);
        canvas.Children.Add(center);
        canvas.Children.Add(new Ellipse
        {
            Width = t * 0.45, Height = t * 0.45, Fill = new SolidColorBrush(Color.Parse("#1A1D23")),
            [Canvas.LeftProperty] = t + t * 0.275, [Canvas.TopProperty] = t + t * 0.275,
        });
        Children.Add(canvas);

        PointerPressed += (_, e) => { e.Pointer.Capture(this); e.Handled = true; Update(e.GetPosition(this)); };
        PointerMoved += (_, e) => { if (e.Pointer.Captured == this) Update(e.GetPosition(this)); };
        PointerReleased += (_, e) => { Set(0); e.Pointer.Capture(null); };
        PointerCaptureLost += (_, _) => Set(0);
    }

    private void Update(Point p)
    {
        double dx = p.X - Bounds.Width / 2, dy = p.Y - Bounds.Height / 2;
        double dead = Bounds.Width * 0.1;
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
        foreach (var (b, arm) in _arms)
        {
            int bit = 1 << (int)b;
            bool on = (bits & bit) != 0;
            if (on != ((_current & bit) != 0)) _session.SetButton(b, on);
            arm.Background = on ? ArmPressed : Arm;
        }
        _current = bits;
    }
}
