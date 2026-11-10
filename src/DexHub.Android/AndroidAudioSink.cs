using Android.Media;
using DexHub.Core.Emulation;

namespace DexHub.Mobile;

/// <summary>Tonausgabe über Android AudioTrack (16 Bit Stereo, Streaming).</summary>
public sealed class AndroidAudioSink : IAudioSink
{
    private AudioTrack? _track;
    private int _sampleRate;
    private long _framesWritten;
    private short[] _buffer = [];

    public string Description => _track is null ? "aus" : $"AudioTrack {_sampleRate} Hz";

    public void Open(int sampleRate)
    {
        _sampleRate = sampleRate;
        int minBytes = AudioTrack.GetMinBufferSize(sampleRate, ChannelOut.Stereo, Encoding.Pcm16bit);
        _track = new AudioTrack.Builder()
            .SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Game)!
                .SetContentType(AudioContentType.Music)!
                .Build()!)
            .SetAudioFormat(new AudioFormat.Builder()
                .SetSampleRate(sampleRate)!
                .SetEncoding(Encoding.Pcm16bit)!
                .SetChannelMask(ChannelOut.Stereo)!
                .Build()!)
            .SetBufferSizeInBytes(Math.Max(minBytes * 2, sampleRate / 5 * 4)) // ca. 200 ms
            .SetTransferMode(AudioTrackMode.Stream)
            .Build();
        _track.Play();
    }

    public void Push(ReadOnlySpan<short> interleavedStereo)
    {
        if (_track is null || interleavedStereo.IsEmpty) return;
        if (_buffer.Length < interleavedStereo.Length) _buffer = new short[interleavedStereo.Length];
        interleavedStereo.CopyTo(_buffer);
        // Nicht blockieren: was nicht mehr passt, fällt weg (der Emulator taktet selbst)
        int written = _track.Write(_buffer, 0, interleavedStereo.Length, WriteMode.NonBlocking);
        if (written > 0) _framesWritten += written / 2;
    }

    public double QueuedSeconds
    {
        get
        {
            if (_track is null || _sampleRate == 0) return 0;
            long played = _track.PlaybackHeadPosition & 0xFFFFFFFFL;
            return Math.Max(0, _framesWritten - played) / (double)_sampleRate;
        }
    }

    public void Dispose()
    {
        try
        {
            _track?.Stop();
            _track?.Release();
        }
        catch (Java.Lang.IllegalStateException) { }
        _track = null;
    }
}
