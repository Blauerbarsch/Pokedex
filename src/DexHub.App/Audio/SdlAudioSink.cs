using DexHub.App.Input;
using DexHub.Core.Emulation;
using Silk.NET.SDL;

namespace DexHub.App.Audio;

/// <summary>Tonausgabe über SDL2 (läuft auf Windows, Mac, Linux und Android).</summary>
public sealed unsafe class SdlAudioSink : IAudioSink
{
    private const ushort AudioS16Lsb = 0x8010; // 16-Bit signed, Little-Endian
    private const uint InitAudio = 0x00000010;

    private Sdl? _sdl;
    private uint _device;
    private int _bytesPerSecond;

    public string Description { get; private set; } = "nicht geöffnet";

    public void Open(int sampleRate)
    {
        _sdl = SdlApi.Get();
        lock (SdlApi.Lock)
        {
            if (_sdl.InitSubSystem(InitAudio) < 0)
                throw new InvalidOperationException("SDL-Audio konnte nicht gestartet werden: " + _sdl.GetErrorS());

            var driver = _sdl.GetCurrentAudioDriverS() ?? "?";

            var desired = new AudioSpec
            {
                Freq = sampleRate,
                Format = AudioS16Lsb,
                Channels = 2,
                Samples = 1024,
            };
            AudioSpec obtained;

            // allowed_changes = 0: SDL rechnet bei Bedarf selbst auf die Rate der Soundkarte um
            _device = _sdl.OpenAudioDevice((byte*)null, 0, &desired, &obtained, 0);
            if (_device == 0)
                throw new InvalidOperationException($"Kein Audiogerät (Treiber „{driver}“): " + _sdl.GetErrorS());

            _bytesPerSecond = sampleRate * 2 * sizeof(short);
            _sdl.PauseAudioDevice(_device, 0); // 0 = abspielen

            Description = $"SDL-Treiber „{driver}“, {obtained.Freq} Hz, {obtained.Channels} Kanäle, Gerät {_device}";
            if (driver is "dummy" or "disk")
                Description += " – ACHTUNG: kein echter Audiotreiber gefunden";
        }
    }

    public void Push(ReadOnlySpan<short> interleavedStereo)
    {
        if (_device == 0 || _sdl is null) return;
        fixed (short* p = interleavedStereo)
        {
            if (_sdl.QueueAudio(_device, p, (uint)(interleavedStereo.Length * sizeof(short))) < 0 && !_reportedPushError)
            {
                _reportedPushError = true;
                Console.WriteLine("[DexHub] QueueAudio fehlgeschlagen: " + _sdl.GetErrorS());
            }
        }
    }
    private bool _reportedPushError;

    public double QueuedSeconds =>
        _device == 0 || _sdl is null ? 0 : _sdl.GetQueuedAudioSize(_device) / (double)_bytesPerSecond;

    public void Dispose()
    {
        if (_device != 0 && _sdl is not null)
        {
            _sdl.CloseAudioDevice(_device);
            _device = 0;
        }
    }
}
