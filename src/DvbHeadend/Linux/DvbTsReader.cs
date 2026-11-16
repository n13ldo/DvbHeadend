using DvbHeadend.Tuners;

namespace DvbHeadend.Linux;

/// <summary>
/// Reads the whole transport stream of the tuned mux: a demux filter on the
/// "all PIDs" pseudo-PID (0x2000) routed to the dvr0 device.
/// </summary>
internal sealed unsafe class DvbTsReader : ITsSource
{
    private int _demuxFd;
    private int _dvrFd;

    public long Overflows { get; private set; }

    private DvbTsReader(int demuxFd, int dvrFd)
    {
        _demuxFd = demuxFd;
        _dvrFd = dvrFd;
    }

    public static DvbTsReader Open(string adapterDir)
    {
        string dvrPath = Path.Combine(adapterDir, "dvr0");
        string demuxPath = Path.Combine(adapterDir, "demux0");

        int dvr = Native.Open(dvrPath, Native.O_RDONLY | Native.O_NONBLOCK);
        if (dvr < 0)
        {
            throw Native.Error($"open {dvrPath}");
        }
        // A full mux is up to ~40 Mbit/s; give the kernel ring buffer room for scheduling hiccups.
        Native.IoctlValue(dvr, DvbApi.DMX_SET_BUFFER_SIZE, 8 * 1024 * 1024);

        int demux = Native.Open(demuxPath, Native.O_RDWR);
        if (demux < 0)
        {
            var error = Native.Error($"open {demuxPath}");
            Native.Close(dvr);
            throw error;
        }

        // struct dmx_pes_filter_params { __u16 pid; input; output; pes_type; __u32 flags; }
        byte* filter = stackalloc byte[DvbApi.PesFilterParamsSize];
        new Span<byte>(filter, DvbApi.PesFilterParamsSize).Clear();
        *(ushort*)filter = DvbApi.AllPids;
        *(uint*)(filter + 4) = DvbApi.DMX_IN_FRONTEND;
        *(uint*)(filter + 8) = DvbApi.DMX_OUT_TS_TAP;
        *(uint*)(filter + 12) = DvbApi.DMX_PES_OTHER;
        *(uint*)(filter + 16) = DvbApi.DMX_IMMEDIATE_START;
        if (Native.Ioctl(demux, DvbApi.DMX_SET_PES_FILTER, filter) < 0)
        {
            var error = Native.Error($"DMX_SET_PES_FILTER on {demuxPath}");
            Native.Close(demux);
            Native.Close(dvr);
            throw error;
        }
        return new DvbTsReader(demux, dvr);
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        var pfd = new Native.PollFd { Fd = _dvrFd, Events = Native.POLLIN };
        int ready = Native.Poll(&pfd, 1, timeoutMs);
        if (ready <= 0)
        {
            return 0;
        }

        fixed (byte* p = buffer)
        {
            nint n = Native.Read(_dvrFd, p, (nuint)buffer.Length);
            if (n >= 0)
            {
                return (int)n;
            }
            int errno = Native.LastError;
            if (errno == Native.EAGAIN || errno == Native.EINTR)
            {
                return 0;
            }
            if (errno == Native.EOVERFLOW)
            {
                // Kernel buffer overran; data was dropped but the stream carries on.
                Overflows++;
                return 0;
            }
            throw Native.Error("read dvr0");
        }
    }

    public void Dispose()
    {
        if (_demuxFd >= 0)
        {
            Native.Ioctl(_demuxFd, DvbApi.DMX_STOP, null);
            Native.Close(_demuxFd);
            _demuxFd = -1;
        }
        if (_dvrFd >= 0)
        {
            Native.Close(_dvrFd);
            _dvrFd = -1;
        }
    }
}
