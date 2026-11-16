using DvbHeadend.Model;

namespace DvbHeadend.Tuners;

public sealed class TunerSignal
{
    public bool Locked { get; set; }
    public bool HasSignal { get; set; }
    public double? StrengthDbm { get; set; }
    public double? StrengthPercent { get; set; }
    public double? SnrDb { get; set; }
    public double? SnrPercent { get; set; }
    public double? BitErrorRate { get; set; }
    public long? UncorrectedBlocks { get; set; }
}

/// <summary>A raw transport stream coming off a tuned mux.</summary>
public interface ITsSource : IDisposable
{
    /// <summary>Blocks for up to <paramref name="timeoutMs"/>; returns 0 when nothing arrived.</summary>
    int Read(Span<byte> buffer, int timeoutMs);
}

public interface ITuner : IDisposable
{
    string Id { get; }
    string Name { get; }

    /// <summary>Tunes and waits for lock. Returns false if no lock within the timeout.</summary>
    bool Tune(MuxDefinition mux, TimeSpan timeout, CancellationToken cancel);

    ITsSource OpenStream();

    TunerSignal GetSignal();

    /// <summary>Closes the frontend so the hardware can power down.</summary>
    void Release();
}
