namespace DvbHeadend.Ts;

public delegate void PacketHandler(ReadOnlySpan<byte> packet);

public delegate void SectionHandler(int pid, ReadOnlySpan<byte> section);

public static class TsPacket
{
    public const int Size = 188;
    public const byte Sync = 0x47;
    public const int NullPid = 0x1FFF;

    public static int Pid(ReadOnlySpan<byte> p) => ((p[1] & 0x1F) << 8) | p[2];
    public static bool PayloadStart(ReadOnlySpan<byte> p) => (p[1] & 0x40) != 0;
    public static bool TransportError(ReadOnlySpan<byte> p) => (p[1] & 0x80) != 0;
    public static bool HasPayload(ReadOnlySpan<byte> p) => (p[3] & 0x10) != 0;
    public static int Continuity(ReadOnlySpan<byte> p) => p[3] & 0x0F;

    /// <summary>Offset of the payload, or -1 if the packet has none.</summary>
    public static int PayloadOffset(ReadOnlySpan<byte> p)
    {
        int afc = (p[3] >> 4) & 3;
        if ((afc & 1) == 0)
        {
            return -1;
        }
        int offset = 4;
        if (afc == 3)
        {
            offset += 1 + p[4];
        }
        return offset < Size ? offset : -1;
    }
}

/// <summary>MPEG-2 CRC32 (poly 0x04C11DB7, init 0xFFFFFFFF, no reflection).</summary>
public static class Crc32Mpeg
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i << 24;
            for (int bit = 0; bit < 8; bit++)
            {
                c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : c << 1;
            }
            table[i] = c;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc = (crc << 8) ^ Table[((crc >> 24) ^ b) & 0xFF];
        }
        return crc;
    }
}

/// <summary>Cuts an arbitrary byte stream into aligned 188-byte packets, resyncing if needed.</summary>
public sealed class TsPacketizer
{
    private readonly byte[] _carry = new byte[TsPacket.Size];
    private int _carryLength;

    public long SyncLosses { get; private set; }

    public void Push(ReadOnlySpan<byte> data, PacketHandler handler)
    {
        if (_carryLength > 0)
        {
            int need = TsPacket.Size - _carryLength;
            if (data.Length < need)
            {
                data.CopyTo(_carry.AsSpan(_carryLength));
                _carryLength += data.Length;
                return;
            }
            data[..need].CopyTo(_carry.AsSpan(_carryLength));
            _carryLength = 0;
            handler(_carry);
            data = data[need..];
        }

        int i = 0;
        while (data.Length - i >= TsPacket.Size)
        {
            if (data[i] != TsPacket.Sync)
            {
                SyncLosses++;
                int next = data[(i + 1)..].IndexOf(TsPacket.Sync);
                if (next < 0)
                {
                    return;
                }
                i += next + 1;
                continue;
            }
            handler(data.Slice(i, TsPacket.Size));
            i += TsPacket.Size;
        }

        var rest = data[i..];
        if (rest.Length > 0)
        {
            int sync = rest.IndexOf(TsPacket.Sync);
            if (sync >= 0)
            {
                rest = rest[sync..];
                rest.CopyTo(_carry);
                _carryLength = rest.Length;
            }
        }
    }
}

/// <summary>Reassembles PSI/SI sections from the packets of one PID and CRC-checks them.</summary>
public sealed class SectionAssembler
{
    private const int MaxSection = 4096 + 3;

    private readonly int _pid;
    private readonly byte[] _buffer = new byte[MaxSection];
    private int _length;
    private int _expected;
    private bool _active;
    private int _lastContinuity = -1;

    public long CrcErrors { get; private set; }

    public SectionAssembler(int pid)
    {
        _pid = pid;
    }

    public void Push(ReadOnlySpan<byte> packet, SectionHandler handler)
    {
        int offset = TsPacket.PayloadOffset(packet);
        if (offset < 0)
        {
            return;
        }

        int cc = TsPacket.Continuity(packet);
        if (_lastContinuity >= 0 && cc == _lastContinuity)
        {
            return; // duplicate packet
        }
        if (_lastContinuity >= 0 && cc != ((_lastContinuity + 1) & 0x0F))
        {
            _active = false; // lost packets: whatever was in flight is unusable
        }
        _lastContinuity = cc;

        var payload = packet[offset..];
        if (TsPacket.PayloadStart(packet))
        {
            int pointer = payload[0];
            payload = payload[1..];
            if (pointer > payload.Length)
            {
                _active = false;
                return;
            }
            if (_active)
            {
                Feed(payload[..pointer], handler, continuationOnly: true);
            }
            _active = false;
            Feed(payload[pointer..], handler, continuationOnly: false);
        }
        else if (_active)
        {
            Feed(payload, handler, continuationOnly: false);
        }
    }

    private void Feed(ReadOnlySpan<byte> data, SectionHandler handler, bool continuationOnly)
    {
        while (data.Length > 0)
        {
            if (!_active)
            {
                if (continuationOnly || data[0] == 0xFF)
                {
                    return; // stuffing until the end of the packet
                }
                _active = true;
                _length = 0;
                _expected = -1;
            }

            int want = _expected < 0 ? 3 - _length : _expected - _length;
            int take = Math.Min(want, data.Length);
            data[..take].CopyTo(_buffer.AsSpan(_length));
            _length += take;
            data = data[take..];

            if (_expected < 0 && _length >= 3)
            {
                _expected = 3 + (((_buffer[1] & 0x0F) << 8) | _buffer[2]);
                if (_expected > MaxSection)
                {
                    _active = false;
                    return;
                }
            }

            if (_expected >= 0 && _length == _expected)
            {
                _active = false;
                Emit(handler);
            }
        }
    }

    private void Emit(SectionHandler handler)
    {
        var section = _buffer.AsSpan(0, _length);
        bool syntax = (section[1] & 0x80) != 0;
        if (syntax)
        {
            if (section.Length < 12 || Crc32Mpeg.Compute(section) != 0)
            {
                CrcErrors++;
                return;
            }
        }
        handler(_pid, section);
    }
}

/// <summary>Builds sections and packetizes them. Used for the per-client PAT, and by tests.</summary>
public static class PsiBuilder
{
    public static byte[] BuildSection(byte tableId, int extension, int version, ReadOnlySpan<byte> body,
        byte sectionNumber = 0, byte lastSectionNumber = 0, bool privateBit = false)
    {
        int sectionLength = 5 + body.Length + 4;
        var section = new byte[3 + sectionLength];
        section[0] = tableId;
        section[1] = (byte)(0x80 | (privateBit ? 0x40 : 0x00) | 0x30 | ((sectionLength >> 8) & 0x0F));
        section[2] = (byte)sectionLength;
        section[3] = (byte)(extension >> 8);
        section[4] = (byte)extension;
        section[5] = (byte)(0xC0 | ((version & 0x1F) << 1) | 0x01);
        section[6] = sectionNumber;
        section[7] = lastSectionNumber;
        body.CopyTo(section.AsSpan(8));
        uint crc = Crc32Mpeg.Compute(section.AsSpan(0, section.Length - 4));
        section[^4] = (byte)(crc >> 24);
        section[^3] = (byte)(crc >> 16);
        section[^2] = (byte)(crc >> 8);
        section[^1] = (byte)crc;
        return section;
    }

    public static byte[] BuildPat(int transportStreamId, int version, IEnumerable<(int Program, int Pid)> programs)
    {
        var body = new List<byte>();
        foreach (var (program, pid) in programs)
        {
            body.Add((byte)(program >> 8));
            body.Add((byte)program);
            body.Add((byte)(0xE0 | (pid >> 8)));
            body.Add((byte)pid);
        }
        return BuildSection(0x00, transportStreamId, version, body.ToArray());
    }

    /// <summary>Wraps one section into as many TS packets as it needs.</summary>
    public static List<byte[]> Packetize(int pid, ReadOnlySpan<byte> section, ref int continuity)
    {
        var packets = new List<byte[]>();
        bool first = true;
        while (section.Length > 0)
        {
            var packet = new byte[TsPacket.Size];
            packet.AsSpan().Fill(0xFF);
            packet[0] = TsPacket.Sync;
            packet[1] = (byte)((first ? 0x40 : 0x00) | ((pid >> 8) & 0x1F));
            packet[2] = (byte)pid;
            packet[3] = (byte)(0x10 | (continuity & 0x0F));
            continuity = (continuity + 1) & 0x0F;

            int offset = 4;
            if (first)
            {
                packet[offset++] = 0; // pointer_field
            }
            int take = Math.Min(section.Length, TsPacket.Size - offset);
            section[..take].CopyTo(packet.AsSpan(offset));
            section = section[take..];
            packets.Add(packet);
            first = false;
        }
        return packets;
    }
}
