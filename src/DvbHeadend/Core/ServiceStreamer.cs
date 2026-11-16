using System.Threading.Channels;
using DvbHeadend.Ts;

namespace DvbHeadend.Core;

/// <summary>
/// Turns the full mux into a single-programme transport stream for one client:
/// a rewritten PAT listing just this service, its PMT, and every elementary
/// stream the PMT references. Output is queued in chunks for an async consumer.
/// </summary>
public sealed class ServiceStreamer : IPacketSink
{
    private const int ChunkPackets = 56;
    private const int MaxQueuedChunks = 512; // ~5 MB; beyond this the client is too slow and loses data

    private readonly int _serviceId;
    private readonly Channel<byte[]> _channel;
    private readonly bool[] _forward = new bool[8192];
    private readonly SectionAssembler _patAssembler = new(0);
    private readonly SectionHandler _onPat;
    private readonly SectionHandler _onPmt;
    private SectionAssembler? _pmtAssembler;
    private int _pmtPid = -1;
    private int _pmtVersion = -1;
    private int _transportStreamId;
    private byte[]? _patPacket;
    private int _patContinuity;
    private bool _started;
    private byte[] _chunk = new byte[ChunkPackets * TsPacket.Size];
    private int _chunkLength;
    private long _chunkStartedTicks;

    public ChannelReader<byte[]> Reader => _channel.Reader;
    public long DroppedChunks { get; private set; }
    public long BytesOut { get; private set; }
    public string? EndReason { get; private set; }

    public ServiceStreamer(int serviceId)
    {
        _serviceId = serviceId;
        _onPat = OnPat;
        _onPmt = OnPmt;
        _channel = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(MaxQueuedChunks)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            },
            _ => DroppedChunks++);
    }

    public void OnPacket(ReadOnlySpan<byte> packet)
    {
        int pid = TsPacket.Pid(packet);
        if (pid == 0)
        {
            _patAssembler.Push(packet, _onPat);
            if (_started && _patPacket is not null)
            {
                // Replace the broadcast PAT with ours, at the broadcaster's repetition rate.
                _patPacket[3] = (byte)(0x10 | _patContinuity);
                _patContinuity = (_patContinuity + 1) & 0x0F;
                Append(_patPacket);
            }
            return;
        }

        if (pid == _pmtPid)
        {
            _pmtAssembler!.Push(packet, _onPmt);
        }

        if (_started && _forward[pid])
        {
            Append(packet);
        }
    }

    private void OnPat(int pid, ReadOnlySpan<byte> section)
    {
        var pat = PatTable.Parse(section);
        if (pat is null)
        {
            return;
        }
        int pmtPid = -1;
        foreach (var (program, programPid) in pat.Programs)
        {
            if (program == _serviceId)
            {
                pmtPid = programPid;
            }
        }
        if (pmtPid < 0 || (pmtPid == _pmtPid && pat.TransportStreamId == _transportStreamId))
        {
            return;
        }

        _pmtPid = pmtPid;
        _pmtVersion = -1;
        _transportStreamId = pat.TransportStreamId;
        _pmtAssembler = new SectionAssembler(pmtPid);
        int cc = 0;
        byte[] patSection = PsiBuilder.BuildPat(pat.TransportStreamId, pat.Version, [(_serviceId, pmtPid)]);
        _patPacket = PsiBuilder.Packetize(0, patSection, ref cc)[0];
    }

    private void OnPmt(int pid, ReadOnlySpan<byte> section)
    {
        var pmt = PmtTable.Parse(section);
        if (pmt is null || pmt.ProgramNumber != _serviceId || pmt.Version == _pmtVersion)
        {
            return;
        }
        _pmtVersion = pmt.Version;

        Array.Clear(_forward);
        _forward[_pmtPid] = true;
        if (pmt.PcrPid != TsPacket.NullPid)
        {
            _forward[pmt.PcrPid] = true;
        }
        foreach (var stream in pmt.Streams)
        {
            _forward[stream.Pid] = true;
        }

        if (!_started)
        {
            // Open the stream with PAT then PMT so players can lock on immediately.
            _started = true;
            _patPacket![3] = (byte)(0x10 | _patContinuity);
            _patContinuity = (_patContinuity + 1) & 0x0F;
            Append(_patPacket);
            int cc = 0;
            foreach (byte[] packet in PsiBuilder.Packetize(_pmtPid, section, ref cc))
            {
                Append(packet);
            }
        }
    }

    private void Append(ReadOnlySpan<byte> packet)
    {
        long now = Environment.TickCount64;
        if (_chunkLength == 0)
        {
            _chunkStartedTicks = now;
        }
        packet.CopyTo(_chunk.AsSpan(_chunkLength));
        _chunkLength += TsPacket.Size;

        // Flush full chunks, or partial ones after 100 ms so low-bitrate radio isn't laggy.
        if (_chunkLength == _chunk.Length || now - _chunkStartedTicks >= 100)
        {
            byte[] output = _chunkLength == _chunk.Length ? _chunk : _chunk.AsSpan(0, _chunkLength).ToArray();
            if (_chunkLength == _chunk.Length)
            {
                _chunk = new byte[ChunkPackets * TsPacket.Size];
            }
            _chunkLength = 0;
            BytesOut += output.Length;
            _channel.Writer.TryWrite(output);
        }
    }

    public void OnSessionEnded(string reason)
    {
        EndReason = reason;
        _channel.Writer.TryComplete();
    }
}
