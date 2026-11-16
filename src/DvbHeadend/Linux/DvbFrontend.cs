using System.Runtime.CompilerServices;
using System.Text;
using DvbHeadend.Model;
using DvbHeadend.Tuners;

namespace DvbHeadend.Linux;

/// <summary>/dev/dvb/adapterN/frontend0 — tuning and signal statistics.</summary>
internal sealed unsafe class DvbFrontend : IDisposable
{
    private int _fd;

    public string DevicePath { get; }

    private DvbFrontend(int fd, string path)
    {
        _fd = fd;
        DevicePath = path;
    }

    public static DvbFrontend Open(string path, bool readOnly = false)
    {
        int flags = (readOnly ? Native.O_RDONLY : Native.O_RDWR) | Native.O_NONBLOCK;
        int fd = Native.Open(path, flags);
        if (fd < 0)
        {
            throw Native.Error($"open {path}");
        }
        return new DvbFrontend(fd, path);
    }

    public string GetName()
    {
        byte* info = stackalloc byte[DvbApi.FrontendInfoSize];
        new Span<byte>(info, DvbApi.FrontendInfoSize).Clear();
        if (Native.Ioctl(_fd, DvbApi.FE_GET_INFO, info) < 0)
        {
            return "Unknown frontend";
        }
        var name = new ReadOnlySpan<byte>(info, 128);
        int end = name.IndexOf((byte)0);
        if (end < 0)
        {
            end = name.Length;
        }
        return Encoding.ASCII.GetString(name[..end]).Trim();
    }

    public List<uint> GetDeliverySystems()
    {
        var result = new List<uint>();
        byte* props = stackalloc byte[DvbApi.DtvPropertySize];
        new Span<byte>(props, DvbApi.DtvPropertySize).Clear();
        *(uint*)props = DvbApi.DTV_ENUM_DELSYS;
        if (GetProperties(props, 1) < 0)
        {
            return result;
        }
        byte* buffer = props + DvbApi.DtvPropertyUnionOffset;
        uint length = Unsafe.ReadUnaligned<uint>(buffer + 32);
        for (int i = 0; i < Math.Min(length, 32u); i++)
        {
            result.Add(buffer[i]);
        }
        return result;
    }

    public void Tune(MuxDefinition mux)
    {
        SetProperties([(DvbApi.DTV_CLEAR, 0)]);

        var props = new List<(uint Cmd, uint Value)>
        {
            (DvbApi.DTV_DELIVERY_SYSTEM, mux.System == DeliverySystem.DVBT2 ? DvbApi.SYS_DVBT2 : DvbApi.SYS_DVBT),
            (DvbApi.DTV_FREQUENCY, checked((uint)mux.FrequencyHz)),
            (DvbApi.DTV_BANDWIDTH_HZ, (uint)mux.BandwidthHz),
            (DvbApi.DTV_INVERSION, DvbApi.INVERSION_AUTO),
            (DvbApi.DTV_MODULATION, DvbApi.QAM_AUTO),
            (DvbApi.DTV_CODE_RATE_HP, DvbApi.FEC_AUTO),
            (DvbApi.DTV_CODE_RATE_LP, DvbApi.FEC_AUTO),
            (DvbApi.DTV_TRANSMISSION_MODE, DvbApi.TRANSMISSION_MODE_AUTO),
            (DvbApi.DTV_GUARD_INTERVAL, DvbApi.GUARD_INTERVAL_AUTO),
            (DvbApi.DTV_HIERARCHY, DvbApi.HIERARCHY_AUTO)
        };
        if (mux.System == DeliverySystem.DVBT2)
        {
            props.Add((DvbApi.DTV_STREAM_ID, mux.PlpId.HasValue ? (uint)mux.PlpId.Value : DvbApi.NO_STREAM_ID_FILTER));
        }
        props.Add((DvbApi.DTV_TUNE, 0));
        SetProperties(props);
    }

    public uint ReadStatus()
    {
        uint status = 0;
        if (Native.Ioctl(_fd, DvbApi.FE_READ_STATUS, &status) < 0)
        {
            return 0;
        }
        return status;
    }

    public TunerSignal ReadSignal()
    {
        var signal = new TunerSignal();
        uint status = ReadStatus();
        signal.Locked = (status & DvbApi.FE_HAS_LOCK) != 0;
        signal.HasSignal = (status & DvbApi.FE_HAS_SIGNAL) != 0;

        uint[] cmds =
        [
            DvbApi.DTV_STAT_SIGNAL_STRENGTH, DvbApi.DTV_STAT_CNR,
            DvbApi.DTV_STAT_POST_ERROR_BIT_COUNT, DvbApi.DTV_STAT_POST_TOTAL_BIT_COUNT,
            DvbApi.DTV_STAT_ERROR_BLOCK_COUNT
        ];
        int size = cmds.Length * DvbApi.DtvPropertySize;
        byte* props = stackalloc byte[size];
        new Span<byte>(props, size).Clear();
        for (int i = 0; i < cmds.Length; i++)
        {
            *(uint*)(props + i * DvbApi.DtvPropertySize) = cmds[i];
        }
        if (GetProperties(props, cmds.Length) < 0)
        {
            return signal;
        }

        var (strengthScale, strength) = ReadStat(props, 0);
        if (strengthScale == DvbApi.FE_SCALE_DECIBEL)
        {
            signal.StrengthDbm = strength / 1000.0;
        }
        else if (strengthScale == DvbApi.FE_SCALE_RELATIVE)
        {
            signal.StrengthPercent = strength * 100.0 / 65535.0;
        }

        var (cnrScale, cnr) = ReadStat(props, 1);
        if (cnrScale == DvbApi.FE_SCALE_DECIBEL)
        {
            signal.SnrDb = cnr / 1000.0;
        }
        else if (cnrScale == DvbApi.FE_SCALE_RELATIVE)
        {
            signal.SnrPercent = cnr * 100.0 / 65535.0;
        }

        var (errScale, errors) = ReadStat(props, 2);
        var (totalScale, total) = ReadStat(props, 3);
        if (errScale == DvbApi.FE_SCALE_COUNTER && totalScale == DvbApi.FE_SCALE_COUNTER && total > 0)
        {
            signal.BitErrorRate = (double)errors / total;
        }

        var (blockScale, blocks) = ReadStat(props, 4);
        if (blockScale == DvbApi.FE_SCALE_COUNTER)
        {
            signal.UncorrectedBlocks = blocks;
        }
        return signal;
    }

    private static (byte Scale, long Value) ReadStat(byte* props, int index)
    {
        // struct dtv_fe_stats { __u8 len; struct { __u8 scale; __s64 value; } stat[4]; } packed
        byte* stats = props + index * DvbApi.DtvPropertySize + DvbApi.DtvPropertyUnionOffset;
        if (stats[0] == 0)
        {
            return (DvbApi.FE_SCALE_NOT_AVAILABLE, 0);
        }
        return (stats[1], Unsafe.ReadUnaligned<long>(stats + 2));
    }

    private void SetProperties(IReadOnlyList<(uint Cmd, uint Value)> values)
    {
        int size = values.Count * DvbApi.DtvPropertySize;
        byte* props = stackalloc byte[size];
        new Span<byte>(props, size).Clear();
        for (int i = 0; i < values.Count; i++)
        {
            byte* p = props + i * DvbApi.DtvPropertySize;
            *(uint*)p = values[i].Cmd;
            Unsafe.WriteUnaligned(p + DvbApi.DtvPropertyUnionOffset, values[i].Value);
        }

        byte* header = stackalloc byte[DvbApi.DtvPropertiesSize];
        new Span<byte>(header, DvbApi.DtvPropertiesSize).Clear();
        *(uint*)header = (uint)values.Count;
        *(byte**)(header + DvbApi.DtvPropertiesPointerOffset) = props;
        if (Native.Ioctl(_fd, DvbApi.FE_SET_PROPERTY, header) < 0)
        {
            throw Native.Error($"FE_SET_PROPERTY on {DevicePath}");
        }
    }

    private int GetProperties(byte* props, int count)
    {
        byte* header = stackalloc byte[DvbApi.DtvPropertiesSize];
        new Span<byte>(header, DvbApi.DtvPropertiesSize).Clear();
        *(uint*)header = (uint)count;
        *(byte**)(header + DvbApi.DtvPropertiesPointerOffset) = props;
        return Native.Ioctl(_fd, DvbApi.FE_GET_PROPERTY, header);
    }

    public void Dispose()
    {
        if (_fd >= 0)
        {
            Native.Close(_fd);
            _fd = -1;
        }
    }
}
