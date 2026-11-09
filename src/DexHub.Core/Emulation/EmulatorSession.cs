using System.Collections.Concurrent;
using System.Diagnostics;
using DexHub.Core.Libretro;
using DexHub.Core.Models;
using DexHub.Core.Settings;

namespace DexHub.Core.Emulation;

/// <summary>
/// Ein laufendes Spiel. Der Emulator läuft in einem eigenen Thread;
/// die Oberfläche holt sich nur die fertigen Bilder ab und gibt Tasten weiter.
/// </summary>
public sealed unsafe class EmulatorSession : ICoreHost, IDisposable
{
    public GameEntry Game { get; }
    private readonly string _corePath;
    private IAudioSink _audio;

    private Thread? _thread;
    private volatile bool _stopRequested;
    private volatile bool _paused;
    private volatile int _stateRequest; // 0 = nichts, 1 = speichern, 2 = laden

    // ---------- Ereignisse (kommen aus dem Emulator-Thread!) ----------
    /// <summary>Kurze Meldung für die Anzeige im Spielfenster.</summary>
    public event Action<string>? Message;
    /// <summary>Spiel konnte nicht gestartet werden oder ist abgestürzt.</summary>
    public event Action<string>? Failed;
    /// <summary>Der Spielstand wurde auf die Festplatte geschrieben (Pfad).</summary>
    public event Action<string>? SaveWritten;
    /// <summary>Emulator-Thread ist beendet.</summary>
    public event Action? Stopped;
    /// <summary>Select + Start wurden eine Sekunde gehalten: Spiel beenden (für reine Controller-Bedienung).</summary>
    public event Action? QuitRequested;
    private int _quitComboFrames;
    /// <summary>Eine Zusatztaste auf dem Controller wurde gedrückt, die das Fenster erledigen muss (z. B. DS-Layout).</summary>
    public event Action<HotkeyAction>? HotkeyPressed;

    /// <summary>Name des Cores, z. B. "melondsds_libretro" – Schlüssel für die Emulator-Optionen.</summary>
    public string CoreName => Path.GetFileNameWithoutExtension(_corePath);

    /// <summary>Eigene Werte für Emulator-Optionen, werden vor dem Spielstart gesetzt.</summary>
    public Dictionary<string, string> CoreOptions { get; set; } = [];

    private readonly ConcurrentQueue<(string Key, string Value)> _optionChanges = new();

    /// <summary>Emulator-Option während des Spiels ändern (wird vor dem nächsten Bild übernommen).</summary>
    public void RequestOptionChange(string key, string value) => _optionChanges.Enqueue((key, value));

    public EmulatorSession(GameEntry game, string corePath, IAudioSink audio)
    {
        Game = game;
        _corePath = corePath;
        _audio = audio;
    }

    public string SaveRamPath => Path.Combine(AppSettings.SavesFolder,
        Path.GetFileNameWithoutExtension(Game.RomPath) + ".srm");

    /// <summary>Schnellspeicher-Platz 0–9 (Platz 0 = ".state", wie früher).</summary>
    public int StateSlot
    {
        get => _stateSlot;
        set => _stateSlot = (value % 10 + 10) % 10;
    }
    private volatile int _stateSlot;

    public string StatePath => Path.Combine(AppSettings.StatesFolder,
        Path.GetFileNameWithoutExtension(Game.RomPath) + (_stateSlot == 0 ? ".state" : $".state{_stateSlot}"));

    /// <summary>Gibt es auf dem aktuellen Platz schon eine Schnellspeicherung?</summary>
    public bool StateSlotUsed => File.Exists(StatePath);

    /// <summary>Automatische Sicherung beim Schließen, wird beim nächsten Start geladen.</summary>
    public string AutoStatePath => Path.Combine(AppSettings.StatesFolder,
        Path.GetFileNameWithoutExtension(Game.RomPath) + ".auto.state");

    /// <summary>Beim Start dort weitermachen, wo zuletzt aufgehört wurde.</summary>
    public bool AutoResume { get; set; } = true;

    // =====================================================================
    // Steuerung von außen (UI-Thread)
    // =====================================================================

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(RunLoop) { IsBackground = true, Name = "Emulator" };
        _thread.Start();
    }

    /// <summary>Zusätzliche Eingabequelle, z. B. Controller. Wird im Emulator-Thread abgefragt.</summary>
    public IInputProvider? InputProvider { get; set; }

    /// <summary>Lautstärke 0.0 – 1.0</summary>
    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 1f);
    }
    private volatile float _volume = 0.8f;

    /// <summary>Faktor für den Schnellvorlauf, 0 = so schnell wie möglich.</summary>
    public int FastForwardSpeed { get; set; } = 3;

    /// <summary>Halten oder Umschalten.</summary>
    public FastForwardMode FastForwardMode { get; set; } = FastForwardMode.Hold;

    private volatile bool _fastForwardKey;
    private bool _fastForwardPressedBefore, _fastForwardToggled;

    /// <summary>Zustand der Schnellvorlauf-Taste (Tastatur). Was das bedeutet, hängt vom Modus ab.</summary>
    public void SetFastForward(bool pressed) => _fastForwardKey = pressed;

    /// <summary>Berechnet, ob gerade vorgespult wird (einmal pro Bild im Emulator-Thread).</summary>
    private bool ResolveFastForward(bool pressed)
    {
        bool risingEdge = pressed && !_fastForwardPressedBefore;
        _fastForwardPressedBefore = pressed;

        if (FastForwardMode == FastForwardMode.Hold)
        {
            _fastForwardToggled = false;
            return pressed;
        }

        if (risingEdge)
        {
            _fastForwardToggled = !_fastForwardToggled;
            Message?.Invoke(_fastForwardToggled ? "Schnellvorlauf an" : "Schnellvorlauf aus");
        }
        return _fastForwardToggled;
    }

    public bool IsPaused => _paused;
    public void TogglePause() => _paused = !_paused;
    public void RequestSaveState() => _stateRequest = 1;
    public void RequestLoadState() => _stateRequest = 2;

    public void Dispose()
    {
        _stopRequested = true;
        _thread?.Join(5000);
    }

    // ---------- Eingabe ----------
    private int _buttons;          // Tastatur, Bitmaske, Bit n = JoypadButton n
    private int _effectiveButtons; // Tastatur + Controller, gilt für das aktuelle Bild
    private volatile short _pointerX, _pointerY;
    private volatile bool _pointerPressed;

    public void SetButton(JoypadButton button, bool pressed)
    {
        int bit = 1 << (int)button;
        int old, updated;
        do
        {
            old = _buttons;
            updated = pressed ? old | bit : old & ~bit;
        } while (Interlocked.CompareExchange(ref _buttons, updated, old) != old);
    }

    /// <summary>Touchscreen/Maus. x und y von 0 bis 1 über das ganze Bild.</summary>
    public void SetPointer(double x, double y, bool pressed)
    {
        _pointerX = ToPointerCoord(x);
        _pointerY = ToPointerCoord(y);
        _pointerPressed = pressed;
    }

    private static short ToPointerCoord(double v) =>
        (short)Math.Clamp((v * 2 - 1) * 0x7FFF, -0x7FFF, 0x7FFF);

    // ---------- Bild ----------
    private readonly object _frameLock = new();
    private uint[] _front = [], _back = [];
    private int _frontW, _frontH;
    private long _frameId;

    public (int Width, int Height) FrameSize
    {
        get { lock (_frameLock) return (_frontW, _frontH); }
    }

    /// <summary>
    /// Kopiert das neueste Bild (BGRA, 32 Bit) nach dst, falls es neu ist und die Größe passt.
    /// </summary>
    public bool CopyFrame(ref long lastFrameId, IntPtr dst, int dstRowBytes, int width, int height)
    {
        lock (_frameLock)
        {
            if (_frameId == lastFrameId || _frontW != width || _frontH != height) return false;
            fixed (uint* src = _front)
            {
                for (int y = 0; y < height; y++)
                    Buffer.MemoryCopy(src + y * width, (byte*)dst + y * dstRowBytes, dstRowBytes, width * 4);
            }
            lastFrameId = _frameId;
            return true;
        }
    }

    // =====================================================================
    // Emulator-Thread
    // =====================================================================

    private LibretroCore? _core;
    private byte[]? _lastWrittenSram;

    private void RunLoop()
    {
        try
        {
            _core = new LibretroCore(_corePath, this, AppSettings.SystemFolder, AppSettings.SavesFolder);
            foreach (var (key, value) in CoreOptions) _core.OptionOverrides[key] = value;
            _core.LoadGame(Game.RomPath);
            CoreOptionStore.Save(CoreName, _core.OptionDefinitions);
            LoadSram();

            var timing = _core.AvInfo.Timing;
            OpenAudio(timing.SampleRate);

            double fps = timing.Fps > 1 ? timing.Fps : 60;
            long ticksPerFrame = (long)(Stopwatch.Frequency / fps);
            var clock = Stopwatch.StartNew();
            long nextFrame = clock.ElapsedTicks;
            int frameCounter = 0;

            Message?.Invoke($"{Game.Title} gestartet ({_core.LibraryName})");
            TryAutoResume();
            Log($"Core {_core.LibraryName} {_core.LibraryVersion}, {fps:0.##} fps");

            while (!_stopRequested && !_core.ShutdownRequested)
            {
                HandleStateRequest();
                while (_optionChanges.TryDequeue(out var change)) _core.SetOption(change.Key, change.Value);

                if (_paused)
                {
                    Thread.Sleep(16);
                    nextFrame = clock.ElapsedTicks;
                    continue;
                }

                // Eingaben sammeln: Tastatur (vom UI-Thread) + Controller
                var pad = PollInputProvider();
                Volatile.Write(ref _effectiveButtons, Volatile.Read(ref _buttons) | pad.Buttons);
                bool fastForward = ResolveFastForward(_fastForwardKey || pad.Has(HotkeyAction.FastForward));
                UpdateTouchCursor(pad, frameCounter);
                HandlePadHotkeys(pad);
                CheckQuitCombo();

                _core.RunFrame();
                FlushAudio(fastForward);

                // Etwa alle 5 Sekunden prüfen, ob das Spiel gespeichert hat
                if (++frameCounter % 300 == 0) WriteSramIfChanged();

                // Takt halten: auf den Zeitpunkt des nächsten Bildes warten
                if (fastForward)
                    nextFrame += FastForwardSpeed > 0 ? ticksPerFrame / FastForwardSpeed : 0;
                else
                    nextFrame += ticksPerFrame;
                long now = clock.ElapsedTicks;
                if (now - nextFrame > ticksPerFrame * 5) nextFrame = now; // zu weit hinterher -> neu anfangen
                WaitUntil(clock, nextFrame);
            }

            WriteSramIfChanged();
            var finalSram = _core.SaveRam;
            if (!finalSram.IsEmpty)
                Log($"Spielstand beim Beenden: Prüfsumme {Checksum(finalSram)}");
            if (frameCounter > 60) WriteAutoState(); // nicht sichern, wenn das Spiel gar nicht richtig lief
        }
        catch (Exception ex)
        {
            Log("Spiel abgebrochen: " + ex);
            Failed?.Invoke(ex.Message);
        }
        finally
        {
            _core?.Dispose();
            _core = null;
            _audio.Dispose();
            Stopped?.Invoke();
        }
    }

    private static void WaitUntil(Stopwatch clock, long targetTicks)
    {
        while (true)
        {
            long remaining = targetTicks - clock.ElapsedTicks;
            if (remaining <= 0) return;
            double ms = remaining * 1000.0 / Stopwatch.Frequency;
            if (ms > 2) Thread.Sleep((int)ms - 1); // grob schlafen …
            else Thread.SpinWait(100);             // … und den Rest genau abwarten
        }
    }

    private void OpenAudio(double sampleRate)
    {
        int rate = (int)Math.Round(sampleRate > 0 ? sampleRate : 44100);
        Log($"Core-Tonrate: {sampleRate:0.##} Hz");
        try
        {
            _audio.Open(rate);
            Log("Ton: " + _audio.Description);
        }
        catch (Exception ex)
        {
            Log("Ton fehlgeschlagen: " + ex);
            _audio.Dispose();
            _audio = new NullAudioSink();
            Message?.Invoke("Kein Ton: " + ex.Message);
        }
    }

    private void HandleStateRequest()
    {
        var request = Interlocked.Exchange(ref _stateRequest, 0);
        if (request == 0 || _core is null) return;

        if (request == 1)
        {
            var state = _core.SaveState();
            if (state is null) { Message?.Invoke("Schnellspeichern wird von diesem Core nicht unterstützt."); return; }
            Directory.CreateDirectory(AppSettings.StatesFolder);
            File.WriteAllBytes(StatePath, state);
            Message?.Invoke($"Gesichert auf Platz {_stateSlot}");
        }
        else if (File.Exists(StatePath))
        {
            Message?.Invoke(_core.LoadState(File.ReadAllBytes(StatePath))
                ? $"Platz {_stateSlot} geladen"
                : "Schnellspeicherung konnte nicht geladen werden");
        }
        else
        {
            Message?.Invoke($"Platz {_stateSlot} ist noch leer");
        }
    }

    // ---------- Automatisch fortsetzen ----------

    private void TryAutoResume()
    {
        if (!AutoResume || _core is null || !File.Exists(AutoStatePath)) return;
        if (_core.LoadState(File.ReadAllBytes(AutoStatePath)))
            Message?.Invoke("Fortgesetzt, wo du aufgehört hast");
        else
            Log("Automatische Sicherung konnte nicht geladen werden: " + AutoStatePath);
    }

    private void WriteAutoState()
    {
        try
        {
            var state = _core?.SaveState();
            if (state is null) return;
            var tmp = AutoStatePath + ".tmp";
            File.WriteAllBytes(tmp, state);
            File.Move(tmp, AutoStatePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log("Automatische Sicherung fehlgeschlagen: " + ex.Message);
        }
    }

    // ---------- Spielstand (SRAM) ----------

    private void LoadSram()
    {
        var sram = _core!.SaveRam;
        if (sram.IsEmpty) return;

        // Eigener Spielstand, sonst einen vorhandenen übernehmen (z. B. .sav neben der ROM)
        var source = File.Exists(SaveRamPath) ? SaveRamPath : Saves.SaveLocator.Find(Game);
        if (source is null)
        {
            // Noch kein Spielstand: den leeren Anfangszustand merken, damit erst dann eine Datei
            // entsteht, wenn das Spiel wirklich speichert
            _lastWrittenSram = sram.ToArray();
            return;
        }

        var data = File.ReadAllBytes(source);
        data.AsSpan(0, Math.Min(data.Length, sram.Length)).CopyTo(sram);
        _lastWrittenSram = sram.ToArray();
        Log($"Spielstand übernommen aus {source} ({data.Length} Bytes), Prüfsumme {Checksum(sram)}");
    }

    /// <summary>Kurze Prüfsumme für die Diagnose: ändert sich, sobald sich der Spielstand ändert.</summary>
    private static string Checksum(ReadOnlySpan<byte> data)
    {
        uint hash = 2166136261;
        foreach (var b in data) hash = (hash ^ b) * 16777619;
        return hash.ToString("X8");
    }

    private bool _loggedNoSram;

    private void WriteSramIfChanged()
    {
        if (_core is null) return;
        var sram = _core.SaveRam;
        if (sram.IsEmpty) // dieser Core verwaltet Spielstände selbst
        {
            if (!_loggedNoSram) Log("Core stellt keinen Spielstand-Speicher bereit");
            _loggedNoSram = true;
            return;
        }
        if (_lastWrittenSram is not null && sram.SequenceEqual(_lastWrittenSram)) return;

        var copy = sram.ToArray();
        Directory.CreateDirectory(AppSettings.SavesFolder);
        var tmp = SaveRamPath + ".tmp";
        File.WriteAllBytes(tmp, copy);
        File.Move(tmp, SaveRamPath, overwrite: true);
        _lastWrittenSram = copy;
        Log($"Spielstand geschrieben: {SaveRamPath} ({copy.Length} Bytes, Prüfsumme {Checksum(copy)})");
        SaveWritten?.Invoke(SaveRamPath);
    }

    // =====================================================================
    // ICoreHost – wird vom Core während RunFrame() aufgerufen
    // =====================================================================

    public void OnVideoFrame(IntPtr data, int width, int height, int pitch, RetroPixelFormat format)
    {
        int count = width * height;
        if (_back.Length != count) _back = new uint[count];

        var srcBase = (byte*)data;
        fixed (uint* dstBase = _back)
        {
            for (int y = 0; y < height; y++)
            {
                var dst = dstBase + y * width;
                var row = srcBase + y * pitch;
                switch (format)
                {
                    case RetroPixelFormat.XRGB8888:
                        var src32 = (uint*)row;
                        for (int x = 0; x < width; x++) dst[x] = src32[x] | 0xFF000000;
                        break;

                    case RetroPixelFormat.RGB565:
                        var src565 = (ushort*)row;
                        for (int x = 0; x < width; x++)
                        {
                            uint p = src565[x];
                            uint r = (p >> 11) & 0x1F, g = (p >> 5) & 0x3F, b = p & 0x1F;
                            dst[x] = 0xFF000000 | ((r << 3 | r >> 2) << 16) | ((g << 2 | g >> 4) << 8) | (b << 3 | b >> 2);
                        }
                        break;

                    default: // XRGB1555
                        var src555 = (ushort*)row;
                        for (int x = 0; x < width; x++)
                        {
                            uint p = src555[x];
                            uint r = (p >> 10) & 0x1F, g = (p >> 5) & 0x1F, b = p & 0x1F;
                            dst[x] = 0xFF000000 | ((r << 3 | r >> 2) << 16) | ((g << 3 | g >> 2) << 8) | (b << 3 | b >> 2);
                        }
                        break;
                }
            }
        }

        // Fertiges Bild nach vorne tauschen
        lock (_frameLock)
        {
            (_front, _back) = (_back, _front);
            _frontW = width;
            _frontH = height;
            _frameId++;
        }
    }

    private short[] _audioBuffer = new short[8192];
    private int _audioCount;

    public void OnAudio(ReadOnlySpan<short> samples)
    {
        if (_audioCount + samples.Length > _audioBuffer.Length)
            Array.Resize(ref _audioBuffer, Math.Max(_audioBuffer.Length * 2, _audioCount + samples.Length));
        samples.CopyTo(_audioBuffer.AsSpan(_audioCount));
        _audioCount += samples.Length;
    }

    private void FlushAudio(bool fastForward)
    {
        var samples = _audioBuffer.AsSpan(0, _audioCount);
        _audioCount = 0;
        if (samples.IsEmpty) return;

        if (fastForward)
        {
            // Beim Vorspulen nur jedes n-te Sample abspielen: der Ton läuft schneller (und höher),
            // passt aber zur Geschwindigkeit des Bildes – wie bei einer schnell abgespielten Kassette.
            int factor = FastForwardSpeed > 0 ? FastForwardSpeed : 4;
            int frames = samples.Length / 2, kept = 0;
            for (int f = 0; f < frames; f += factor, kept++)
            {
                samples[kept * 2] = samples[f * 2];
                samples[kept * 2 + 1] = samples[f * 2 + 1];
            }
            samples = samples[..(kept * 2)];

            // Bei "unbegrenzt" läuft das Bild evtl. noch schneller: dann einfach Überschuss verwerfen
            if (_audio.QueuedSeconds > 0.1) return;
        }
        else if (_audio.QueuedSeconds > 0.15)
        {
            // Liegt schon zu viel Ton in der Warteschlange: verwerfen, damit der Ton nicht hinterherhinkt
            return;
        }

        float volume = _volume;
        if (volume < 0.999f)
            for (int i = 0; i < samples.Length; i++)
                samples[i] = (short)(samples[i] * volume);

        _audio.Push(samples);
    }

    // ---------- Touch-Zeiger für Controller (DS) ----------

    /// <summary>Touch-Zeiger per rechtem Stick einschalten (für DS-Spiele).</summary>
    public bool UseTouchCursor { get; set; }

    /// <summary>
    /// Bereich, in dem sich der Zeiger bewegen darf (0–1 über das ganze Bild).
    /// Standard: untere Bildhälfte = unterer DS-Bildschirm.
    /// </summary>
    public double TouchAreaTop { get; set; } = 0.5;

    private double _cursorX = 0.5, _cursorY = 0.75;
    private volatile bool _cursorVisible, _cursorPressed;
    private bool _padTouching;
    private int _cursorLastMovedFrame = -1000;

    /// <summary>Position des Controller-Zeigers für die Anzeige im Spielfenster.</summary>
    public (double X, double Y, bool Visible, bool Pressed) TouchCursor =>
        (Volatile.Read(ref _cursorX), Volatile.Read(ref _cursorY), _cursorVisible, _cursorPressed);

    private volatile bool _keyboardTouch;
    /// <summary>Touch-Taste auf der Tastatur (tippt an der Stelle des Controller-Zeigers).</summary>
    public void SetKeyboardTouch(bool pressed) => _keyboardTouch = pressed;

    private void UpdateTouchCursor(InputSnapshot pad, int frame)
    {
        if (!UseTouchCursor) return;

        const float deadzone = 0.2f;
        const double speed = 0.015; // Anteil des Bildes pro Bild bei voll ausgelenktem Stick

        float sx = Math.Abs(pad.CursorX) > deadzone ? pad.CursorX : 0;
        float sy = Math.Abs(pad.CursorY) > deadzone ? pad.CursorY : 0;
        if (sx != 0 || sy != 0)
        {
            // Quadratische Kennlinie: kleine Auslenkung = feine Bewegung
            Volatile.Write(ref _cursorX, Math.Clamp(_cursorX + Math.Sign(sx) * sx * sx * speed, 0, 1));
            Volatile.Write(ref _cursorY, Math.Clamp(_cursorY + Math.Sign(sy) * sy * sy * speed, TouchAreaTop, 1));
            _cursorLastMovedFrame = frame;
        }

        bool touch = pad.Has(HotkeyAction.Touch) || _keyboardTouch;
        if (touch)
        {
            SetPointer(_cursorX, _cursorY, true);
            _padTouching = true;
            _cursorLastMovedFrame = frame;
        }
        else if (_padTouching)
        {
            SetPointer(_cursorX, _cursorY, false); // loslassen
            _padTouching = false;
        }

        _cursorPressed = touch;
        _cursorVisible = frame - _cursorLastMovedFrame < 180; // nach ca. 3 s ohne Bewegung ausblenden
    }

    private void CheckQuitCombo()
    {
        int combo = (1 << (int)JoypadButton.Select) | (1 << (int)JoypadButton.Start);
        if ((Volatile.Read(ref _effectiveButtons) & combo) == combo)
        {
            if (++_quitComboFrames == 60) QuitRequested?.Invoke(); // ca. 1 Sekunde
        }
        else
        {
            _quitComboFrames = 0;
        }
    }

    private int _previousPadHotkeys;

    /// <summary>Speichern/Laden per Controller: nur beim Drücken auslösen, nicht solange gehalten.</summary>
    private void HandlePadHotkeys(InputSnapshot pad)
    {
        int newlyPressed = pad.Hotkeys & ~_previousPadHotkeys;
        _previousPadHotkeys = pad.Hotkeys;
        if ((newlyPressed & (1 << (int)HotkeyAction.SaveState)) != 0) RequestSaveState();
        if ((newlyPressed & (1 << (int)HotkeyAction.LoadState)) != 0) RequestLoadState();

        foreach (var action in new[] { HotkeyAction.NextDsLayout, HotkeyAction.SwapDsScreens, HotkeyAction.NextEffect,
                                       HotkeyAction.PreviousStateSlot, HotkeyAction.NextStateSlot,
                                       HotkeyAction.HuntPlus, HotkeyAction.HuntMinus })
            if ((newlyPressed & (1 << (int)action)) != 0) HotkeyPressed?.Invoke(action);
    }

    /// <summary>
    /// Controller nur auswerten, wenn das Spielfenster vorne ist – sonst würden bei mehreren
    /// gleichzeitig laufenden Spielen alle auf denselben Controller reagieren.
    /// </summary>
    public bool AcceptControllerInput
    {
        get => _acceptControllerInput;
        set => _acceptControllerInput = value;
    }
    private volatile bool _acceptControllerInput = true;

    private InputSnapshot PollInputProvider()
    {
        if (InputProvider is null || !_acceptControllerInput) return default;
        try
        {
            return InputProvider.Poll();
        }
        catch (Exception ex)
        {
            InputProvider = null; // kaputter Controller-Treiber soll das Spiel nicht stören
            Log("Controller deaktiviert: " + ex.Message);
            Message?.Invoke("Controller deaktiviert: " + ex.Message);
            return default;
        }
    }

    /// <summary>Diagnose-Ausgabe im Terminal.</summary>
    private static void Log(string text) => Console.WriteLine("[DexHub] " + text);

    public void PollInput() { }

    public short GetInputState(uint port, uint device, uint index, uint id)
    {
        if (port != 0) return 0;
        switch (device & 0xFF)
        {
            case Retro.DeviceJoypad:
                int buttons = Volatile.Read(ref _effectiveButtons);
                if (id == Retro.JoypadMask) return (short)(buttons & 0xFFFF);
                return (short)((buttons >> (int)id) & 1);

            case Retro.DevicePointer when index == 0:
                return id switch
                {
                    0 => _pointerX,
                    1 => _pointerY,
                    2 => (short)(_pointerPressed ? 1 : 0),
                    _ => (short)0,
                };

            default:
                return 0;
        }
    }

    public void OnCoreMessage(string message) => Message?.Invoke(message);
}
