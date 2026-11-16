using System.Diagnostics;
using DvbHeadend.Model;
using DvbHeadend.Ts;
using DvbHeadend.Tuners;

namespace DvbHeadend.Core;

/// <summary>Receives every packet of a mux on the reader thread. Must be fast and never block.</summary>
public interface IPacketSink
{
    void OnPacket(ReadOnlySpan<byte> packet);

    /// <summary>The mux went away (tuner pre-empted, hardware error, shutdown).</summary>
    void OnSessionEnded(string reason);
}

/// <summary>
/// One tuner locked to one mux. A dedicated thread reads the transport stream
/// and fans packets out to the SI collector and every attached sink.
/// </summary>
public sealed class MuxSession : IDisposable
{
    private readonly ITsSource _source;
    private readonly ILogger _log;
    private readonly Thread _thread;
    private readonly TsPacketizer _packetizer = new();
    private readonly sbyte[] _continuity = new sbyte[8192];
    private readonly Stopwatch _rateClock = Stopwatch.StartNew();
    private volatile IPacketSink[] _sinks = [];
    private readonly object _sinkLock = new();
    private volatile bool _stop;
    private long _rateBytes;

    public ITuner Tuner { get; }
    public MuxDefinition Mux { get; }
    public SiCollector Si { get; } = new();
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public long Packets { get; private set; }
    public long ContinuityErrors { get; private set; }
    public long TransportErrors { get; private set; }
    public double BitrateMbps { get; private set; }
    public bool Faulted { get; private set; }

    public MuxSession(ITuner tuner, MuxDefinition mux, ILogger log)
    {
        Tuner = tuner;
        Mux = mux;
        _log = log;
        _continuity.AsSpan().Fill(-1);
        _source = tuner.OpenStream();
        _thread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = $"mux-{tuner.Id}",
            Priority = ThreadPriority.AboveNormal
        };
    }

    public void Start() => _thread.Start();

    public void AddSink(IPacketSink sink)
    {
        lock (_sinkLock)
        {
            _sinks = [.. _sinks, sink];
        }
    }

    public void RemoveSink(IPacketSink sink)
    {
        lock (_sinkLock)
        {
            _sinks = _sinks.Where(s => !ReferenceEquals(s, sink)).ToArray();
        }
    }

    private void ReadLoop()
    {
        var buffer = new byte[TsPacket.Size * 1024];
        PacketHandler handler = OnPacket;
        try
        {
            while (!_stop)
            {
                int n = _source.Read(buffer, 200);
                if (n > 0)
                {
                    _rateBytes += n;
                    _packetizer.Push(buffer.AsSpan(0, n), handler);
                }
                if (_rateClock.ElapsedMilliseconds >= 1000)
                {
                    BitrateMbps = _rateBytes * 8 / _rateClock.Elapsed.TotalSeconds / 1e6;
                    _rateBytes = 0;
                    _rateClock.Restart();
                }
            }
        }
        catch (Exception ex)
        {
            Faulted = true;
            _log.LogError(ex, "{Tuner}: stream from {Mux} failed", Tuner.Id, Mux);
            foreach (var sink in _sinks)
            {
                sink.OnSessionEnded("tuner error: " + ex.Message);
            }
        }
    }

    private void OnPacket(ReadOnlySpan<byte> packet)
    {
        Packets++;
        if (TsPacket.TransportError(packet))
        {
            TransportErrors++;
            return;
        }

        int pid = TsPacket.Pid(packet);
        if (pid != TsPacket.NullPid && TsPacket.HasPayload(packet))
        {
            int cc = TsPacket.Continuity(packet);
            int last = _continuity[pid];
            if (last >= 0 && cc != ((last + 1) & 0x0F) && cc != last)
            {
                ContinuityErrors++;
            }
            _continuity[pid] = (sbyte)cc;
        }

        Si.Process(packet);
        foreach (var sink in _sinks)
        {
            sink.OnPacket(packet);
        }
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive && Thread.CurrentThread != _thread)
        {
            _thread.Join(2000);
        }
        _source.Dispose();
    }
}
