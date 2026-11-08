namespace DexHub.Core.Emulation;

/// <summary>Tonausgabe. Die eigentliche Umsetzung (SDL) steckt in der App, damit Core plattformneutral bleibt.</summary>
public interface IAudioSink : IDisposable
{
    void Open(int sampleRate);

    /// <summary>16-Bit-Stereo-Samples, abwechselnd links/rechts.</summary>
    void Push(ReadOnlySpan<short> interleavedStereo);

    /// <summary>Wie viel Ton noch in der Warteschlange liegt.</summary>
    double QueuedSeconds { get; }
}

/// <summary>Stumme Tonausgabe, falls keine Audio-Hardware verfügbar ist.</summary>
public sealed class NullAudioSink : IAudioSink
{
    public void Open(int sampleRate) { }
    public void Push(ReadOnlySpan<short> interleavedStereo) { }
    public double QueuedSeconds => 0;
    public void Dispose() { }
}
