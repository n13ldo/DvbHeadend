namespace DvbHeadend.Ts;

/// <summary>Common header of a section with section_syntax_indicator = 1.</summary>
public readonly ref struct SectionHeader
{
    public readonly byte TableId;
    public readonly int Extension;
    public readonly int Version;
    public readonly bool Current;
    public readonly byte SectionNumber;
    public readonly byte LastSectionNumber;
    /// <summary>Everything between the 8-byte header and the CRC.</summary>
    public readonly ReadOnlySpan<byte> Body;

    public SectionHeader(ReadOnlySpan<byte> section)
    {
        TableId = section[0];
        Extension = (section[3] << 8) | section[4];
        Version = (section[5] >> 1) & 0x1F;
        Current = (section[5] & 1) != 0;
        SectionNumber = section[6];
        LastSectionNumber = section[7];
        Body = section[8..^4];
    }
}

internal ref struct DescriptorReader
{
    private ReadOnlySpan<byte> _data;

    public DescriptorReader(ReadOnlySpan<byte> data)
    {
        _data = data;
    }

    public bool Next(out byte tag, out ReadOnlySpan<byte> payload)
    {
        if (_data.Length < 2 || _data.Length < 2 + _data[1])
        {
            tag = 0;
            payload = default;
            return false;
        }
        tag = _data[0];
        payload = _data.Slice(2, _data[1]);
        _data = _data[(2 + payload.Length)..];
        return true;
    }
}

public sealed record PatTable(int TransportStreamId, int Version, IReadOnlyList<(int Program, int Pid)> Programs)
{
    public static PatTable? Parse(ReadOnlySpan<byte> section)
    {
        if (section.Length < 12 || section[0] != 0x00)
        {
            return null;
        }
        var header = new SectionHeader(section);
        var programs = new List<(int, int)>();
        var body = header.Body;
        for (int i = 0; i + 4 <= body.Length; i += 4)
        {
            int program = (body[i] << 8) | body[i + 1];
            int pid = ((body[i + 2] & 0x1F) << 8) | body[i + 3];
            if (program != 0)
            {
                programs.Add((program, pid));
            }
        }
        return new PatTable(header.Extension, header.Version, programs);
    }
}

public sealed record PmtStream(int StreamType, int Pid, string Codec, string Kind, string? Language);

public sealed record PmtTable(int ProgramNumber, int Version, int PcrPid, bool Scrambled, IReadOnlyList<PmtStream> Streams)
{
    public static PmtTable? Parse(ReadOnlySpan<byte> section)
    {
        if (section.Length < 16 || section[0] != 0x02)
        {
            return null;
        }
        var header = new SectionHeader(section);
        var body = header.Body;
        if (body.Length < 4)
        {
            return null;
        }
        int pcrPid = ((body[0] & 0x1F) << 8) | body[1];
        int programInfoLength = ((body[2] & 0x0F) << 8) | body[3];
        if (4 + programInfoLength > body.Length)
        {
            return null;
        }
        bool scrambled = HasDescriptor(body.Slice(4, programInfoLength), 0x09);

        var streams = new List<PmtStream>();
        int i = 4 + programInfoLength;
        while (i + 5 <= body.Length)
        {
            int streamType = body[i];
            int pid = ((body[i + 1] & 0x1F) << 8) | body[i + 2];
            int infoLength = ((body[i + 3] & 0x0F) << 8) | body[i + 4];
            if (i + 5 + infoLength > body.Length)
            {
                break;
            }
            var descriptors = body.Slice(i + 5, infoLength);
            scrambled |= HasDescriptor(descriptors, 0x09);
            streams.Add(Classify(streamType, pid, descriptors));
            i += 5 + infoLength;
        }
        return new PmtTable(header.Extension, header.Version, pcrPid, scrambled, streams);
    }

    private static bool HasDescriptor(ReadOnlySpan<byte> descriptors, byte wanted)
    {
        var reader = new DescriptorReader(descriptors);
        while (reader.Next(out byte tag, out _))
        {
            if (tag == wanted)
            {
                return true;
            }
        }
        return false;
    }

    private static PmtStream Classify(int streamType, int pid, ReadOnlySpan<byte> descriptors)
    {
        string? language = null;
        string? privateCodec = null;
        string privateKind = "data";
        var reader = new DescriptorReader(descriptors);
        while (reader.Next(out byte tag, out var payload))
        {
            switch (tag)
            {
                case 0x0A when payload.Length >= 3:
                    language = System.Text.Encoding.ASCII.GetString(payload[..3]);
                    break;
                case 0x6A:
                    privateCodec = "ac3";
                    privateKind = "audio";
                    break;
                case 0x7A:
                    privateCodec = "eac3";
                    privateKind = "audio";
                    break;
                case 0x7C:
                    privateCodec = "aac";
                    privateKind = "audio";
                    break;
                case 0x59:
                    privateCodec = "dvbsub";
                    privateKind = "subtitle";
                    if (payload.Length >= 3)
                    {
                        language = System.Text.Encoding.ASCII.GetString(payload[..3]);
                    }
                    break;
                case 0x56:
                    privateCodec = "teletext";
                    privateKind = "teletext";
                    break;
            }
        }

        return streamType switch
        {
            0x01 or 0x02 => new PmtStream(streamType, pid, "mpeg2video", "video", language),
            0x1B => new PmtStream(streamType, pid, "h264", "video", language),
            0x24 => new PmtStream(streamType, pid, "hevc", "video", language),
            0x03 or 0x04 => new PmtStream(streamType, pid, "mp2", "audio", language),
            0x0F => new PmtStream(streamType, pid, "aac", "audio", language),
            0x11 => new PmtStream(streamType, pid, "aac_latm", "audio", language),
            0x06 => new PmtStream(streamType, pid, privateCodec ?? "private", privateKind, language),
            _ => new PmtStream(streamType, pid, $"type_{streamType:x2}", "data", language)
        };
    }
}

public sealed record SdtService(int ServiceId, int ServiceType, string Provider, string Name, bool Scrambled);

public sealed record SdtSection(int TransportStreamId, int OriginalNetworkId, int Version, byte SectionNumber,
    byte LastSectionNumber, bool Actual, IReadOnlyList<SdtService> Services)
{
    public static SdtSection? Parse(ReadOnlySpan<byte> section)
    {
        if (section.Length < 15 || (section[0] != 0x42 && section[0] != 0x46))
        {
            return null;
        }
        var header = new SectionHeader(section);
        var body = header.Body;
        if (body.Length < 3)
        {
            return null;
        }
        int onid = (body[0] << 8) | body[1];
        var services = new List<SdtService>();
        int i = 3;
        while (i + 5 <= body.Length)
        {
            int serviceId = (body[i] << 8) | body[i + 1];
            bool freeCa = (body[i + 3] & 0x10) != 0;
            int loopLength = ((body[i + 3] & 0x0F) << 8) | body[i + 4];
            if (i + 5 + loopLength > body.Length)
            {
                break;
            }
            int serviceType = 0;
            string provider = "";
            string name = "";
            var reader = new DescriptorReader(body.Slice(i + 5, loopLength));
            while (reader.Next(out byte tag, out var payload))
            {
                if (tag == 0x48 && payload.Length >= 3)
                {
                    serviceType = payload[0];
                    int providerLength = payload[1];
                    if (2 + providerLength < payload.Length)
                    {
                        provider = DvbText.Decode(payload.Slice(2, providerLength));
                        int nameLength = payload[2 + providerLength];
                        if (3 + providerLength + nameLength <= payload.Length)
                        {
                            name = DvbText.Decode(payload.Slice(3 + providerLength, nameLength));
                        }
                    }
                }
            }
            services.Add(new SdtService(serviceId, serviceType, provider, name, freeCa));
            i += 5 + loopLength;
        }
        return new SdtSection(header.Extension, onid, header.Version, header.SectionNumber,
            header.LastSectionNumber, section[0] == 0x42, services);
    }
}

public sealed record LcnEntry(int OriginalNetworkId, int TransportStreamId, int ServiceId, int Lcn, bool Visible);

public sealed record NitTransport(int TransportStreamId, int OriginalNetworkId, long? FrequencyHz);

public sealed record NitSection(int NetworkId, int Version, byte SectionNumber, byte LastSectionNumber, bool Actual,
    string? NetworkName, IReadOnlyList<NitTransport> Transports, IReadOnlyList<LcnEntry> Lcns)
{
    public static NitSection? Parse(ReadOnlySpan<byte> section)
    {
        if (section.Length < 16 || (section[0] != 0x40 && section[0] != 0x41))
        {
            return null;
        }
        var header = new SectionHeader(section);
        var body = header.Body;
        if (body.Length < 4)
        {
            return null;
        }

        string? networkName = null;
        int networkLength = ((body[0] & 0x0F) << 8) | body[1];
        if (2 + networkLength + 2 > body.Length)
        {
            return null;
        }
        var reader = new DescriptorReader(body.Slice(2, networkLength));
        while (reader.Next(out byte tag, out var payload))
        {
            if (tag == 0x40)
            {
                networkName = DvbText.Decode(payload);
            }
        }

        var transports = new List<NitTransport>();
        var lcns = new List<LcnEntry>();
        int i = 2 + networkLength;
        int loopLength = ((body[i] & 0x0F) << 8) | body[i + 1];
        i += 2;
        int end = Math.Min(body.Length, i + loopLength);
        while (i + 6 <= end)
        {
            int tsid = (body[i] << 8) | body[i + 1];
            int onid = (body[i + 2] << 8) | body[i + 3];
            int descriptorLength = ((body[i + 4] & 0x0F) << 8) | body[i + 5];
            if (i + 6 + descriptorLength > end)
            {
                break;
            }
            long? frequency = null;
            var descriptors = new DescriptorReader(body.Slice(i + 6, descriptorLength));
            while (descriptors.Next(out byte tag, out var payload))
            {
                if (tag == 0x5A && payload.Length >= 4)
                {
                    // terrestrial_delivery_system_descriptor: centre frequency in 10 Hz units
                    uint raw = ((uint)payload[0] << 24) | ((uint)payload[1] << 16) | ((uint)payload[2] << 8) | payload[3];
                    frequency = raw * 10L;
                }
                else if (tag == 0x83)
                {
                    // Logical channel descriptor (UK DTG / EACEM): 4 bytes per service
                    for (int j = 0; j + 4 <= payload.Length; j += 4)
                    {
                        int serviceId = (payload[j] << 8) | payload[j + 1];
                        bool visible = (payload[j + 2] & 0x80) != 0;
                        int lcn = ((payload[j + 2] & 0x03) << 8) | payload[j + 3];
                        lcns.Add(new LcnEntry(onid, tsid, serviceId, lcn, visible));
                    }
                }
            }
            transports.Add(new NitTransport(tsid, onid, frequency));
            i += 6 + descriptorLength;
        }
        return new NitSection(header.Extension, header.Version, header.SectionNumber, header.LastSectionNumber,
            section[0] == 0x40, networkName, transports, lcns);
    }
}

public sealed record EitEvent(int EventId, DateTime StartUtc, int DurationSeconds, string Title, string Description,
    string? Genre, bool CompressedText);

public sealed record EitSection(int ServiceId, int TransportStreamId, int OriginalNetworkId, byte TableId,
    IReadOnlyList<EitEvent> Events)
{
    private static readonly DateTime MjdEpoch = new(1858, 11, 17, 0, 0, 0, DateTimeKind.Utc);

    private static readonly string?[] Genres =
    [
        null, "Movie / Drama", "News / Current affairs", "Show / Game show", "Sports", "Children's / Youth",
        "Music / Ballet / Dance", "Arts / Culture", "Social / Political / Economics",
        "Education / Science / Factual", "Leisure / Hobbies", "Special", null, null, null, null
    ];

    public static EitSection? Parse(ReadOnlySpan<byte> section)
    {
        if (section.Length < 18 || section[0] < 0x4E || section[0] > 0x6F)
        {
            return null;
        }
        var header = new SectionHeader(section);
        var body = header.Body;
        if (body.Length < 6)
        {
            return null;
        }
        int tsid = (body[0] << 8) | body[1];
        int onid = (body[2] << 8) | body[3];

        var events = new List<EitEvent>();
        int i = 6;
        while (i + 12 <= body.Length)
        {
            int eventId = (body[i] << 8) | body[i + 1];
            var start = ParseTime(body.Slice(i + 2, 5));
            int duration = Bcd(body[i + 7]) * 3600 + Bcd(body[i + 8]) * 60 + Bcd(body[i + 9]);
            int loopLength = ((body[i + 10] & 0x0F) << 8) | body[i + 11];
            if (i + 12 + loopLength > body.Length)
            {
                break;
            }

            string title = "";
            string shortText = "";
            var extended = new System.Text.StringBuilder();
            string? genre = null;
            bool compressed = false;
            var reader = new DescriptorReader(body.Slice(i + 12, loopLength));
            while (reader.Next(out byte tag, out var payload))
            {
                if (tag == 0x4D && payload.Length >= 5)
                {
                    int nameLength = payload[3];
                    if (4 + nameLength < payload.Length)
                    {
                        var name = payload.Slice(4, nameLength);
                        compressed |= DvbText.IsCompressed(name);
                        title = DvbText.Decode(name);
                        int textLength = payload[4 + nameLength];
                        if (5 + nameLength + textLength <= payload.Length)
                        {
                            shortText = DvbText.Decode(payload.Slice(5 + nameLength, textLength));
                        }
                    }
                }
                else if (tag == 0x4E && payload.Length >= 6)
                {
                    int itemsLength = payload[4];
                    if (5 + itemsLength < payload.Length)
                    {
                        int textLength = payload[5 + itemsLength];
                        if (6 + itemsLength + textLength <= payload.Length)
                        {
                            extended.Append(DvbText.Decode(payload.Slice(6 + itemsLength, textLength)));
                        }
                    }
                }
                else if (tag == 0x54 && payload.Length >= 2)
                {
                    genre = Genres[payload[0] >> 4];
                }
            }

            if (start.HasValue)
            {
                string description = extended.Length > 0 ? extended.ToString() : shortText;
                events.Add(new EitEvent(eventId, start.Value, duration, title, description, genre, compressed));
            }
            i += 12 + loopLength;
        }
        return new EitSection(header.Extension, tsid, onid, section[0], events);
    }

    private static int Bcd(byte b) => (b >> 4) * 10 + (b & 0x0F);

    private static DateTime? ParseTime(ReadOnlySpan<byte> t)
    {
        if (t[0] == 0xFF && t[1] == 0xFF)
        {
            return null; // undefined start (NVOD reference)
        }
        int mjd = (t[0] << 8) | t[1];
        return MjdEpoch.AddDays(mjd).AddHours(Bcd(t[2])).AddMinutes(Bcd(t[3])).AddSeconds(Bcd(t[4]));
    }

    /// <summary>Encodes a UTC time as the 5-byte MJD+BCD form. Used by tests.</summary>
    public static byte[] EncodeTime(DateTime utc)
    {
        int mjd = (int)(utc.Date - MjdEpoch).TotalDays;
        return
        [
            (byte)(mjd >> 8), (byte)mjd,
            (byte)(((utc.Hour / 10) << 4) | (utc.Hour % 10)),
            (byte)(((utc.Minute / 10) << 4) | (utc.Minute % 10)),
            (byte)(((utc.Second / 10) << 4) | (utc.Second % 10))
        ];
    }
}
