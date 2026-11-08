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

    public EmulatorSession(GameEntry game, string corePath, IAudioSink audio)
    {
        Game = game;
        _corePath = corePath;
        _audio = audio;
    }

    public string SaveRamPath => Path.Combine(AppSettings.SavesFolder,
        Path.GetFileNameWithoutExtension(Game.RomPath) + ".srm");

    public string StatePath => Path.Combine(AppSettings.StatesFolder,
        Path.GetFileNameWithoutExtension(Game.RomPath) + ".state");

    // =====================================================================
    // Steuerung von außen (UI-Thread)
    // =====================================================================

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(RunLoop) { IsBackground = true, Name = "Emulator" };
        _thread.Start();
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
    private int _buttons; // Bitmaske, Bit n = JoypadButton n
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
            _core.LoadGame(Game.RomPath);
            LoadSram();

            var timing = _core.AvInfo.Timing;
            OpenAudio(timing.SampleRate);

            double fps = timing.Fps > 1 ? timing.Fps : 60;
            long ticksPerFrame = (long)(Stopwatch.Frequency / fps);
            var clock = Stopwatch.StartNew();
            long nextFrame = clock.ElapsedTicks;
            int frameCounter = 0;

            Message?.Invoke($"{Game.Title} gestartet ({_core.LibraryName})");

            while (!_stopRequested && !_core.ShutdownRequested)
            {
                HandleStateRequest();

                if (_paused)
                {
                    Thread.Sleep(16);
                    nextFrame = clock.ElapsedTicks;
                    continue;
                }

                _core.RunFrame();
                FlushAudio();

                // Etwa alle 5 Sekunden prüfen, ob das Spiel gespeichert hat
                if (++frameCounter % 300 == 0) WriteSramIfChanged();

                // Takt halten: auf den Zeitpunkt des nächsten Bildes warten
                nextFrame += ticksPerFrame;
                long now = clock.ElapsedTicks;
                if (now - nextFrame > ticksPerFrame * 5) nextFrame = now; // zu weit hinterher -> neu anfangen
                WaitUntil(clock, nextFrame);
            }

            WriteSramIfChanged();
        }
        catch (Exception ex)
        {
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
        try
        {
            _audio.Open((int)Math.Round(sampleRate > 0 ? sampleRate : 44100));
        }
        catch (Exception ex)
        {
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
            Message?.Invoke("Schnellspeicherung gesichert (F9 zum Laden)");
        }
        else if (File.Exists(StatePath))
        {
            Message?.Invoke(_core.LoadState(File.ReadAllBytes(StatePath))
                ? "Schnellspeicherung geladen"
                : "Schnellspeicherung konnte nicht geladen werden");
        }
        else
        {
            Message?.Invoke("Noch keine Schnellspeicherung vorhanden (F5 zum Speichern)");
        }
    }

    // ---------- Spielstand (SRAM) ----------

    private void LoadSram()
    {
        var sram = _core!.SaveRam;
        if (sram.IsEmpty) return;

        // Eigener Spielstand, sonst einen vorhandenen übernehmen (z. B. .sav neben der ROM)
        var source = File.Exists(SaveRamPath) ? SaveRamPath : Saves.SaveLocator.Find(Game);
        if (source is null) return;

        var data = File.ReadAllBytes(source);
        data.AsSpan(0, Math.Min(data.Length, sram.Length)).CopyTo(sram);
        _lastWrittenSram = sram.ToArray();
    }

    private void WriteSramIfChanged()
    {
        if (_core is null) return;
        var sram = _core.SaveRam;
        if (sram.IsEmpty) return; // dieser Core verwaltet Spielstände selbst
        if (_lastWrittenSram is not null && sram.SequenceEqual(_lastWrittenSram)) return;

        var copy = sram.ToArray();
        Directory.CreateDirectory(AppSettings.SavesFolder);
        var tmp = SaveRamPath + ".tmp";
        File.WriteAllBytes(tmp, copy);
        File.Move(tmp, SaveRamPath, overwrite: true);
        _lastWrittenSram = copy;
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

    private void FlushAudio()
    {
        // Liegt schon zu viel Ton in der Warteschlange, verwerfen wir etwas, damit der Ton nicht hinterherhinkt
        if (_audioCount > 0 && _audio.QueuedSeconds < 0.15)
            _audio.Push(_audioBuffer.AsSpan(0, _audioCount));
        _audioCount = 0;
    }

    public void PollInput() { }

    public short GetInputState(uint port, uint device, uint index, uint id)
    {
        if (port != 0) return 0;
        switch (device & 0xFF)
        {
            case Retro.DeviceJoypad:
                int buttons = Volatile.Read(ref _buttons);
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
