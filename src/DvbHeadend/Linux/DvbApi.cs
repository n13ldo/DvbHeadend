namespace DvbHeadend.Linux;

/// <summary>
/// Constants from linux/dvb/frontend.h and linux/dvb/dmx.h (DVB API v5).
/// ioctl numbers are computed rather than hard-coded so the same binary is
/// correct on arm64, armhf and x64 (struct sizes differ with pointer width).
/// </summary>
internal static class DvbApi
{
    private const uint IocType = 'o';

    private static nuint Ioc(uint dir, uint nr, uint size) => (nuint)((dir << 30) | (size << 16) | (IocType << 8) | nr);
    private static nuint Io(uint nr) => Ioc(0, nr, 0);
    private static nuint Iow(uint nr, uint size) => Ioc(1, nr, size);
    private static nuint Ior(uint nr, uint size) => Ioc(2, nr, size);

    // struct dtv_property is __attribute__((packed)); its union holds a pointer.
    public static readonly int DtvPropertySize = IntPtr.Size == 8 ? 76 : 72;
    public const int DtvPropertyUnionOffset = 16;
    // struct dtv_properties { __u32 num; struct dtv_property *props; }
    public static readonly int DtvPropertiesSize = IntPtr.Size == 8 ? 16 : 8;
    public static readonly int DtvPropertiesPointerOffset = IntPtr.Size == 8 ? 8 : 4;

    public const int FrontendInfoSize = 168;
    public const int PesFilterParamsSize = 20;

    public static readonly nuint FE_GET_INFO = Ior(61, FrontendInfoSize);
    public static readonly nuint FE_READ_STATUS = Ior(69, 4);
    public static readonly nuint FE_SET_PROPERTY = Iow(82, (uint)DtvPropertiesSize);
    public static readonly nuint FE_GET_PROPERTY = Ior(83, (uint)DtvPropertiesSize);

    public static readonly nuint DMX_START = Io(41);
    public static readonly nuint DMX_STOP = Io(42);
    public static readonly nuint DMX_SET_PES_FILTER = Iow(44, PesFilterParamsSize);
    public static readonly nuint DMX_SET_BUFFER_SIZE = Io(45);

    // DTV property commands
    public const uint DTV_TUNE = 1;
    public const uint DTV_CLEAR = 2;
    public const uint DTV_FREQUENCY = 3;
    public const uint DTV_MODULATION = 4;
    public const uint DTV_BANDWIDTH_HZ = 5;
    public const uint DTV_INVERSION = 6;
    public const uint DTV_DELIVERY_SYSTEM = 17;
    public const uint DTV_CODE_RATE_HP = 36;
    public const uint DTV_CODE_RATE_LP = 37;
    public const uint DTV_GUARD_INTERVAL = 38;
    public const uint DTV_TRANSMISSION_MODE = 39;
    public const uint DTV_HIERARCHY = 40;
    public const uint DTV_STREAM_ID = 42;
    public const uint DTV_ENUM_DELSYS = 44;
    public const uint DTV_STAT_SIGNAL_STRENGTH = 62;
    public const uint DTV_STAT_CNR = 63;
    public const uint DTV_STAT_POST_ERROR_BIT_COUNT = 66;
    public const uint DTV_STAT_POST_TOTAL_BIT_COUNT = 67;
    public const uint DTV_STAT_ERROR_BLOCK_COUNT = 68;

    // fe_delivery_system
    public const uint SYS_DVBT = 3;
    public const uint SYS_DVBT2 = 16;

    // "let the demodulator work it out" values
    public const uint INVERSION_AUTO = 2;
    public const uint QAM_AUTO = 6;
    public const uint FEC_AUTO = 9;
    public const uint TRANSMISSION_MODE_AUTO = 2;
    public const uint GUARD_INTERVAL_AUTO = 4;
    public const uint HIERARCHY_AUTO = 4;
    public const uint NO_STREAM_ID_FILTER = 0xFFFFFFFF;

    // fe_status bits
    public const uint FE_HAS_SIGNAL = 0x01;
    public const uint FE_HAS_CARRIER = 0x02;
    public const uint FE_HAS_VITERBI = 0x04;
    public const uint FE_HAS_SYNC = 0x08;
    public const uint FE_HAS_LOCK = 0x10;

    // fecap_scale_params
    public const byte FE_SCALE_NOT_AVAILABLE = 0;
    public const byte FE_SCALE_DECIBEL = 1;
    public const byte FE_SCALE_RELATIVE = 2;
    public const byte FE_SCALE_COUNTER = 3;

    // demux
    public const uint DMX_IN_FRONTEND = 0;
    public const uint DMX_OUT_TS_TAP = 2;
    public const uint DMX_PES_OTHER = 20;
    public const uint DMX_IMMEDIATE_START = 4;
    public const ushort AllPids = 0x2000;
}
