using DexHub.Core.Emulation;
using Silk.NET.SDL;

namespace DexHub.App.Audio;

/// <summary>Tonausgabe über SDL2 (läuft auf Windows, Mac, Linux und Android).</summary>
public sealed unsafe class SdlAudioSink : IAudioSink
{
    private const ushort AudioS16Lsb = 0x8010; // 16-Bit signed, Little-Endian
    private const uint InitAudio = 0x00000010;

    private static Sdl? _sdl;
    private uint _device;
    private int _bytesPerSecond;

    public void Open(int sampleRate)
    {
        _sdl ??= Sdl.GetApi();
        if (_sdl.InitSubSystem(InitAudio) < 0)
            throw new InvalidOperationException("SDL-Audio konnte nicht gestartet werden.");

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
            throw new InvalidOperationException("Kein Audiogerät gefunden.");

        _bytesPerSecond = sampleRate * 2 * sizeof(short);
        _sdl.PauseAudioDevice(_device, 0); // 0 = abspielen
    }

    public void Push(ReadOnlySpan<short> interleavedStereo)
    {
        if (_device == 0 || _sdl is null) return;
        fixed (short* p = interleavedStereo)
            _sdl.QueueAudio(_device, p, (uint)(interleavedStereo.Length * sizeof(short)));
    }

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
