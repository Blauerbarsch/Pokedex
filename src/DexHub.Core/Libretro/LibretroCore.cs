using System.Runtime.InteropServices;

namespace DexHub.Core.Libretro;

/// <summary>Was der Core von seiner Umgebung (dem "Frontend", also uns) braucht.</summary>
public interface ICoreHost
{
    void OnVideoFrame(IntPtr data, int width, int height, int pitch, RetroPixelFormat format);
    void OnAudio(ReadOnlySpan<short> interleavedStereo);
    void PollInput();
    short GetInputState(uint port, uint device, uint index, uint id);
    void OnCoreMessage(string message);
}

/// <summary>
/// Lädt einen libretro-Core (DLL/dylib) und spricht direkt mit ihm.
/// Wichtig: Alle Methoden müssen vom selben Thread aufgerufen werden, und es darf
/// immer nur ein Core gleichzeitig geladen sein (libretro-Cores haben globalen Zustand).
/// </summary>
public sealed unsafe class LibretroCore : IDisposable
{
    private IntPtr _lib;
    private readonly ICoreHost _host;
    private readonly string _corePath;

    // Funktionen des Cores
    private readonly RetroFn.Void _retroInit, _retroDeinit, _retroRun, _retroReset, _retroUnloadGame;
    private readonly RetroFn.GetSystemInfo _getSystemInfo;
    private readonly RetroFn.GetSystemAvInfo _getAvInfo;
    private readonly RetroFn.LoadGame _loadGame;
    private readonly RetroFn.SetControllerPortDevice _setControllerPortDevice;
    private readonly RetroFn.SerializeSize _serializeSize;
    private readonly RetroFn.Serialize _serialize, _unserialize;
    private readonly RetroFn.GetMemoryData _getMemoryData;
    private readonly RetroFn.GetMemorySize _getMemorySize;

    // Unsere Callbacks. Als Felder gespeichert, damit der Garbage Collector sie nicht wegräumt,
    // solange der Core sie noch aufrufen kann.
    private readonly RetroFn.Environment _environmentCb;
    private readonly RetroFn.VideoRefresh _videoCb;
    private readonly RetroFn.AudioSample _audioSampleCb;
    private readonly RetroFn.AudioSampleBatch _audioBatchCb;
    private readonly RetroFn.InputPoll _inputPollCb;
    private readonly RetroFn.InputState _inputStateCb;

    // Speicher, den wir für den Core angelegt haben und am Ende freigeben müssen
    private readonly List<IntPtr> _allocations = [];
    private readonly Dictionary<string, IntPtr> _stringCache = [];
    private IntPtr _romData;

    // Core-Optionen ("Variablen")
    private readonly Dictionary<string, string> _variables = [];
    private bool _variablesChanged;

    private readonly string _systemDirectory;
    private readonly string _saveDirectory;
    private bool _initialized, _gameLoaded;

    public string LibraryName { get; }
    public string LibraryVersion { get; }
    public bool NeedFullPath { get; }
    public RetroPixelFormat PixelFormat { get; private set; } = RetroPixelFormat.XRGB1555;
    public RetroSystemAvInfo AvInfo { get; private set; }
    public bool ShutdownRequested { get; private set; }

    /// <summary>Optionen, die wir abweichend vom Standard setzen wollen.</summary>
    public Dictionary<string, string> OptionOverrides { get; } = [];

    public LibretroCore(string corePath, ICoreHost host, string systemDirectory, string saveDirectory)
    {
        _corePath = corePath;
        _host = host;
        _systemDirectory = systemDirectory;
        _saveDirectory = saveDirectory;
        Directory.CreateDirectory(systemDirectory);
        Directory.CreateDirectory(saveDirectory);

        _lib = NativeLibrary.Load(corePath);
        try
        {
            var apiVersion = Bind<RetroFn.ApiVersion>("retro_api_version")();
            if (apiVersion != 1)
                throw new InvalidOperationException($"Unbekannte libretro-API-Version {apiVersion}.");

            _retroInit = Bind<RetroFn.Void>("retro_init");
            _retroDeinit = Bind<RetroFn.Void>("retro_deinit");
            _retroRun = Bind<RetroFn.Void>("retro_run");
            _retroReset = Bind<RetroFn.Void>("retro_reset");
            _retroUnloadGame = Bind<RetroFn.Void>("retro_unload_game");
            _getSystemInfo = Bind<RetroFn.GetSystemInfo>("retro_get_system_info");
            _getAvInfo = Bind<RetroFn.GetSystemAvInfo>("retro_get_system_av_info");
            _loadGame = Bind<RetroFn.LoadGame>("retro_load_game");
            _setControllerPortDevice = Bind<RetroFn.SetControllerPortDevice>("retro_set_controller_port_device");
            _serializeSize = Bind<RetroFn.SerializeSize>("retro_serialize_size");
            _serialize = Bind<RetroFn.Serialize>("retro_serialize");
            _unserialize = Bind<RetroFn.Serialize>("retro_unserialize");
            _getMemoryData = Bind<RetroFn.GetMemoryData>("retro_get_memory_data");
            _getMemorySize = Bind<RetroFn.GetMemorySize>("retro_get_memory_size");

            _environmentCb = OnEnvironment;
            _videoCb = OnVideoRefresh;
            _audioSampleCb = OnAudioSample;
            _audioBatchCb = OnAudioSampleBatch;
            _inputPollCb = () => _host.PollInput();
            _inputStateCb = (port, device, index, id) => _host.GetInputState(port, device, index, id);

            // Laut libretro-Spezifikation: Environment VOR retro_init setzen
            Bind<RetroFn.SetCallback>("retro_set_environment")(Marshal.GetFunctionPointerForDelegate(_environmentCb));
            Bind<RetroFn.SetCallback>("retro_set_video_refresh")(Marshal.GetFunctionPointerForDelegate(_videoCb));
            Bind<RetroFn.SetCallback>("retro_set_audio_sample")(Marshal.GetFunctionPointerForDelegate(_audioSampleCb));
            Bind<RetroFn.SetCallback>("retro_set_audio_sample_batch")(Marshal.GetFunctionPointerForDelegate(_audioBatchCb));
            Bind<RetroFn.SetCallback>("retro_set_input_poll")(Marshal.GetFunctionPointerForDelegate(_inputPollCb));
            Bind<RetroFn.SetCallback>("retro_set_input_state")(Marshal.GetFunctionPointerForDelegate(_inputStateCb));

            _retroInit();
            _initialized = true;

            var info = new RetroSystemInfo();
            _getSystemInfo(ref info);
            LibraryName = Marshal.PtrToStringUTF8(info.LibraryName) ?? "?";
            LibraryVersion = Marshal.PtrToStringUTF8(info.LibraryVersion) ?? "?";
            NeedFullPath = info.NeedFullpath != 0;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private T Bind<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_lib, name));

    // =====================================================================
    // Öffentliche Steuerung
    // =====================================================================

    public void LoadGame(string romPath)
    {
        var game = new RetroGameInfo { Path = Utf8(romPath) };

        // Manche Cores wollen den Pfad, andere die Daten im Speicher
        if (!NeedFullPath)
        {
            var bytes = File.ReadAllBytes(romPath);
            _romData = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, _romData, bytes.Length);
            game.Data = _romData;
            game.Size = (nuint)bytes.Length;
        }

        if (!_loadGame(ref game))
            throw new InvalidOperationException($"Der Core „{LibraryName}“ konnte das Spiel nicht laden.");
        _gameLoaded = true;

        var av = new RetroSystemAvInfo();
        _getAvInfo(ref av);
        AvInfo = av;

        _setControllerPortDevice(0, Retro.DeviceJoypad);
    }

    /// <summary>Emuliert genau ein Bild (bei Game Boy also ca. 1/60 Sekunde).</summary>
    public void RunFrame() => _retroRun();

    public void Reset() => _retroReset();

    /// <summary>Direkter Zugriff auf einen Speicherbereich des Spiels, z. B. den Spielstand (SRAM).</summary>
    public Span<byte> GetMemory(uint id)
    {
        var ptr = _getMemoryData(id);
        var size = (int)_getMemorySize(id);
        return ptr == IntPtr.Zero || size <= 0 ? Span<byte>.Empty : new Span<byte>((void*)ptr, size);
    }

    public Span<byte> SaveRam => GetMemory(Retro.MemorySaveRam);
    public Span<byte> SystemRam => GetMemory(Retro.MemorySystemRam);

    public byte[]? SaveState()
    {
        var size = (int)_serializeSize();
        if (size <= 0) return null;
        var buffer = new byte[size];
        fixed (byte* p = buffer)
            return _serialize((IntPtr)p, (nuint)size) ? buffer : null;
    }

    public bool LoadState(byte[] state)
    {
        fixed (byte* p = state)
            return _unserialize((IntPtr)p, (nuint)state.Length);
    }

    // =====================================================================
    // Callbacks vom Core
    // =====================================================================

    private void OnVideoRefresh(IntPtr data, uint width, uint height, nuint pitch)
    {
        // data == 0 heißt: Bild ist unverändert ("dupe")
        if (data != IntPtr.Zero)
            _host.OnVideoFrame(data, (int)width, (int)height, (int)pitch, PixelFormat);
    }

    private void OnAudioSample(short left, short right)
    {
        short* s = stackalloc short[2];
        s[0] = left;
        s[1] = right;
        _host.OnAudio(new ReadOnlySpan<short>(s, 2));
    }

    private nuint OnAudioSampleBatch(IntPtr data, nuint frames)
    {
        _host.OnAudio(new ReadOnlySpan<short>((void*)data, (int)frames * 2));
        return frames;
    }

    /// <summary>
    /// Der Core fragt hierüber alles Mögliche ab oder teilt Dinge mit.
    /// Rückgabe false = "kenne ich nicht / kann ich nicht". Das ist für unbekannte Befehle in Ordnung.
    /// </summary>
    private bool OnEnvironment(uint cmd, IntPtr data)
    {
        try
        {
            switch (cmd & ~Retro.Experimental)
            {
                case Retro.EnvGetCanDupe:
                    *(byte*)data = 1;
                    return true;

                case Retro.EnvSetPixelFormat:
                    var fmt = *(int*)data;
                    if (fmt is < 0 or > 2) return false;
                    PixelFormat = (RetroPixelFormat)fmt;
                    return true;

                case Retro.EnvGetSystemDirectory:
                case Retro.EnvGetCoreAssetsDirectory:
                    *(IntPtr*)data = Utf8(_systemDirectory);
                    return true;

                case Retro.EnvGetSaveDirectory:
                    *(IntPtr*)data = Utf8(_saveDirectory);
                    return true;

                case Retro.EnvGetLibretroPath:
                    *(IntPtr*)data = Utf8(_corePath);
                    return true;

                case Retro.EnvGetUsername:
                    *(IntPtr*)data = Utf8("Spieler");
                    return true;

                case Retro.EnvGetLanguage:
                    *(uint*)data = Retro.LanguageGerman;
                    return true;

                case Retro.EnvSetVariables:
                    RegisterVariables((RetroVariable*)data);
                    return true;

                case Retro.EnvGetVariable:
                    return GetVariable((RetroVariable*)data);

                case Retro.EnvSetVariable:
                    if (data == IntPtr.Zero) return true;
                    var sv = (RetroVariable*)data;
                    if (Marshal.PtrToStringUTF8(sv->Key) is { } k && Marshal.PtrToStringUTF8(sv->Value) is { } val)
                    {
                        _variables[k] = val;
                        _variablesChanged = true;
                    }
                    return true;

                case Retro.EnvGetVariableUpdate:
                    *(byte*)data = (byte)(_variablesChanged ? 1 : 0);
                    _variablesChanged = false;
                    return true;

                case Retro.EnvGetCoreOptionsVersion:
                    // false -> der Core fällt auf das einfache SET_VARIABLES zurück
                    return false;

                case Retro.EnvSetGeometry:
                    var av = AvInfo;
                    av.Geometry = *(RetroGameGeometry*)data;
                    AvInfo = av;
                    return true;

                case Retro.EnvSetSystemAvInfo:
                    AvInfo = *(RetroSystemAvInfo*)data;
                    return true;

                case Retro.EnvGetInputBitmasks:
                    return true;

                case Retro.EnvGetAudioVideoEnable:
                    if (data != IntPtr.Zero) *(int*)data = 3; // Bild + Ton
                    return true;

                case Retro.EnvGetFastForwarding:
                    if (data != IntPtr.Zero) *(byte*)data = 0;
                    return true;

                case Retro.EnvGetTargetRefreshRate:
                    *(float*)data = 60f;
                    return true;

                case Retro.EnvSetMessage:
                    // struct retro_message { const char* msg; unsigned frames; }
                    if (Marshal.PtrToStringUTF8(*(IntPtr*)data) is { } msg) _host.OnCoreMessage(msg);
                    return true;

                case Retro.EnvShutdown:
                    ShutdownRequested = true;
                    return true;

                // Diese Meldungen nehmen wir zur Kenntnis, brauchen sie aber (noch) nicht
                case Retro.EnvSetRotation:
                case Retro.EnvSetPerformanceLevel:
                case Retro.EnvSetInputDescriptors:
                case Retro.EnvSetSupportNoGame:
                case Retro.EnvSetSubsystemInfo:
                case Retro.EnvSetControllerInfo:
                case Retro.EnvSetMemoryMaps:
                case Retro.EnvSetSupportAchievements:
                case Retro.EnvSetSerializationQuirks:
                case Retro.EnvSetCoreOptionsDisplay:
                case Retro.EnvSetMinimumAudioLatency:
                case Retro.EnvSetFastForwardingOverride:
                case Retro.EnvSetContentInfoOverride:
                case Retro.EnvSetCoreOptionsUpdateDisplayCallback:
                    return true;

                default:
                    // Alles andere (z. B. Hardware-3D-Rendering) unterstützen wir noch nicht
                    return false;
            }
        }
        catch
        {
            // Exceptions dürfen niemals in den nativen Code zurücklaufen
            return false;
        }
    }

    /// <summary>Format: key = "melonds_xyz", value = "Beschreibung; Standard|Option2|Option3".</summary>
    private void RegisterVariables(RetroVariable* vars)
    {
        if (vars == null) return;
        for (var v = vars; v->Key != IntPtr.Zero; v++)
        {
            var key = Marshal.PtrToStringUTF8(v->Key)!;
            var desc = Marshal.PtrToStringUTF8(v->Value) ?? "";
            var semicolon = desc.IndexOf(';');
            var options = semicolon >= 0 ? desc[(semicolon + 1)..] : desc;
            var defaultValue = options.Split('|')[0].Trim();
            _variables[key] = OptionOverrides.GetValueOrDefault(key, defaultValue);
        }
    }

    private bool GetVariable(RetroVariable* v)
    {
        if (v == null) return false;
        var key = Marshal.PtrToStringUTF8(v->Key);
        if (key is not null &&
            (OptionOverrides.TryGetValue(key, out var value) || _variables.TryGetValue(key, out value)))
        {
            v->Value = Utf8(value);
            return true;
        }
        v->Value = IntPtr.Zero;
        return false;
    }

    /// <summary>Erzeugt einen C-String, der bis zum Entladen des Cores gültig bleibt.</summary>
    private IntPtr Utf8(string s)
    {
        if (_stringCache.TryGetValue(s, out var ptr)) return ptr;
        ptr = Marshal.StringToCoTaskMemUTF8(s);
        _stringCache[s] = ptr;
        _allocations.Add(ptr);
        return ptr;
    }

    // =====================================================================
    // Aufräumen
    // =====================================================================

    public void Dispose()
    {
        if (_gameLoaded) { _retroUnloadGame(); _gameLoaded = false; }
        if (_initialized) { _retroDeinit(); _initialized = false; }
        if (_lib != IntPtr.Zero) { NativeLibrary.Free(_lib); _lib = IntPtr.Zero; }

        foreach (var p in _allocations) Marshal.FreeCoTaskMem(p);
        _allocations.Clear();
        _stringCache.Clear();
        if (_romData != IntPtr.Zero) { Marshal.FreeHGlobal(_romData); _romData = IntPtr.Zero; }
    }
}
