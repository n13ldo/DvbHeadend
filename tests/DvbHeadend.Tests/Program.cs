using System.Text;
using DvbHeadend.Core;
using DvbHeadend.Model;
using DvbHeadend.Ts;
using DvbHeadend.Tuners;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

int failed = 0;
int passed = 0;

void Test(string name, Action body)
{
    try
    {
        body();
        passed++;
        Console.WriteLine($"  ok    {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"  FAIL  {name}: {ex.Message}");
    }
}

void Equal<T>(T expected, T actual, string what = "")
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new Exception($"{what} expected <{expected}> but was <{actual}>");
    }
}

void True(bool condition, string what)
{
    if (!condition)
    {
        throw new Exception(what);
    }
}

static byte[] Descriptor(byte tag, params byte[] payload) => [tag, (byte)payload.Length, .. payload];

static List<byte[]> Collect(int pid, IEnumerable<byte[]> packets)
{
    var sections = new List<byte[]>();
    var assembler = new SectionAssembler(pid);
    foreach (byte[] packet in packets)
    {
        assembler.Push(packet, (_, s) => sections.Add(s.ToArray()));
    }
    return sections;
}

static byte[] PmtSection(int program, int pcrPid, params (byte Type, int Pid)[] streams)
{
    var body = new List<byte> { (byte)(0xE0 | (pcrPid >> 8)), (byte)pcrPid, 0xF0, 0x00 };
    foreach (var (type, pid) in streams)
    {
        body.AddRange([type, (byte)(0xE0 | (pid >> 8)), (byte)pid, 0xF0, 0x00]);
    }
    return PsiBuilder.BuildSection(0x02, program, 3, body.ToArray());
}

static byte[] EsPacket(int pid, int cc, byte fill)
{
    var packet = new byte[188];
    packet.AsSpan().Fill(fill);
    packet[0] = 0x47;
    packet[1] = (byte)(pid >> 8);
    packet[2] = (byte)pid;
    packet[3] = (byte)(0x10 | (cc & 0x0F));
    return packet;
}

Console.WriteLine("Transport stream");

Test("CRC32/MPEG-2 check value", () =>
{
    Equal(0x0376E6E7u, Crc32Mpeg.Compute(Encoding.ASCII.GetBytes("123456789")));
});

Test("section spanning several packets is reassembled", () =>
{
    byte[] section = PsiBuilder.BuildSection(0x42, 0x1041, 1, new byte[700]);
    int cc = 5;
    var sections = Collect(0x11, PsiBuilder.Packetize(0x11, section, ref cc));
    Equal(1, sections.Count);
    True(sections[0].AsSpan().SequenceEqual(section), "section bytes differ");
});

Test("two sections sharing one packet are both delivered", () =>
{
    byte[] a = PsiBuilder.BuildSection(0x4E, 1, 1, new byte[20]);
    byte[] b = PsiBuilder.BuildSection(0x4E, 2, 1, new byte[30]);
    var packet = new byte[188];
    packet.AsSpan().Fill(0xFF);
    packet[0] = 0x47;
    packet[1] = 0x40;
    packet[2] = 0x12;
    packet[3] = 0x10;
    packet[4] = 0;
    a.CopyTo(packet, 5);
    b.CopyTo(packet, 5 + a.Length);
    Equal(2, Collect(0x12, [packet]).Count);
});

Test("section is dropped when a packet goes missing", () =>
{
    byte[] section = PsiBuilder.BuildSection(0x42, 1, 1, new byte[700]);
    int cc = 0;
    var packets = PsiBuilder.Packetize(0x11, section, ref cc);
    packets.RemoveAt(1);
    Equal(0, Collect(0x11, packets).Count);
});

Test("corrupt section fails the CRC and is dropped", () =>
{
    byte[] section = PsiBuilder.BuildSection(0x42, 1, 1, new byte[50]);
    section[20] ^= 0x01;
    int cc = 0;
    Equal(0, Collect(0x11, PsiBuilder.Packetize(0x11, section, ref cc)).Count);
});

Test("packetizer resyncs after garbage and across read boundaries", () =>
{
    var stream = new List<byte> { 0x00, 0x11, 0x22 };
    for (int i = 0; i < 5; i++)
    {
        stream.AddRange(EsPacket(0x100, i, 0xAA));
    }
    var packetizer = new TsPacketizer();
    int count = 0;
    byte[] data = stream.ToArray();
    for (int offset = 0; offset < data.Length; offset += 100)
    {
        packetizer.Push(data.AsSpan(offset, Math.Min(100, data.Length - offset)), p =>
        {
            Equal(0x100, TsPacket.Pid(p));
            count++;
        });
    }
    Equal(5, count);
});

Test("ioctl numbers match the kernel headers (64-bit)", () =>
{
    if (IntPtr.Size != 8)
    {
        return;
    }
    Equal((nuint)0x80A86F3D, DvbHeadend.Linux.DvbApi.FE_GET_INFO);
    Equal((nuint)0x80046F45, DvbHeadend.Linux.DvbApi.FE_READ_STATUS);
    Equal((nuint)0x40106F52, DvbHeadend.Linux.DvbApi.FE_SET_PROPERTY);
    Equal((nuint)0x80106F53, DvbHeadend.Linux.DvbApi.FE_GET_PROPERTY);
    Equal((nuint)0x40146F2C, DvbHeadend.Linux.DvbApi.DMX_SET_PES_FILTER);
    Equal((nuint)0x6F2D, DvbHeadend.Linux.DvbApi.DMX_SET_BUFFER_SIZE);
    Equal(76, DvbHeadend.Linux.DvbApi.DtvPropertySize);
});

Console.WriteLine("Service information");

Test("PAT", () =>
{
    var pat = PatTable.Parse(PsiBuilder.BuildPat(0x1041, 7, [(4164, 0x100), (6720, 0x200)]))!;
    Equal(0x1041, pat.TransportStreamId);
    Equal(2, pat.Programs.Count);
    Equal((6720, 0x200), pat.Programs[1]);
});

Test("PMT stream classification", () =>
{
    var body = new List<byte> { 0xE1, 0x01, 0xF0, 0x00 };
    body.AddRange([0x1B, 0xE1, 0x01, 0xF0, 0x00]);
    byte[] lang = Descriptor(0x0A, (byte)'e', (byte)'n', (byte)'g', 0);
    body.AddRange([0x11, 0xE1, 0x02, 0xF0, (byte)lang.Length, .. lang]);
    byte[] subs = Descriptor(0x59, (byte)'e', (byte)'n', (byte)'g', 0x10, 0, 1, 0, 1);
    body.AddRange([0x06, 0xE1, 0x03, 0xF0, (byte)subs.Length, .. subs]);
    var pmt = PmtTable.Parse(PsiBuilder.BuildSection(0x02, 17540, 2, body.ToArray()))!;
    Equal(0x101, pmt.PcrPid);
    Equal("h264", pmt.Streams[0].Codec);
    Equal("aac_latm", pmt.Streams[1].Codec);
    Equal("eng", pmt.Streams[1].Language);
    Equal("subtitle", pmt.Streams[2].Kind);
});

Test("SDT service names", () =>
{
    byte[] provider = Encoding.ASCII.GetBytes("BBC");
    byte[] name = Encoding.ASCII.GetBytes("BBC ONE Scot");
    byte[] service = Descriptor(0x48, [0x01, (byte)provider.Length, .. provider, (byte)name.Length, .. name]);
    byte[] body = [0x23, 0x3A, 0xFF, 0x10, 0x44, 0xFC, 0x80, (byte)service.Length, .. service];
    var sdt = SdtSection.Parse(PsiBuilder.BuildSection(0x42, 0x1041, 1, body))!;
    Equal(0x233A, sdt.OriginalNetworkId);
    Equal("BBC ONE Scot", sdt.Services[0].Name);
    Equal("BBC", sdt.Services[0].Provider);
    Equal(0x1044, sdt.Services[0].ServiceId);
});

Test("NIT frequency and UK logical channel numbers", () =>
{
    byte[] delivery = Descriptor(0x5A, [0x04, 0x04, 0x71, 0x40, 0x1F, 0x81, 0x00, 0xFF, 0xFF, 0xFF, 0xFF]);
    byte[] lcn = Descriptor(0x83, [0x10, 0x44, 0xFC, 0x01, 0x1A, 0x40, 0x7E, 0xC0]);
    byte[] descriptors = [.. delivery, .. lcn];
    byte[] loop = [0x10, 0x41, 0x23, 0x3A, 0xF0, (byte)descriptors.Length, .. descriptors];
    byte[] network = Descriptor(0x40, Encoding.ASCII.GetBytes("Scotland"));
    byte[] body = [0xF0, (byte)network.Length, .. network, 0xF0, (byte)loop.Length, .. loop];
    var nit = NitSection.Parse(PsiBuilder.BuildSection(0x40, 0x3005, 1, body))!;
    Equal("Scotland", nit.NetworkName);
    Equal(674_000_000L, nit.Transports[0].FrequencyHz!.Value);
    Equal(2, nit.Lcns.Count);
    Equal(1, nit.Lcns[0].Lcn);
    True(nit.Lcns[0].Visible, "LCN 1 should be visible");
    Equal(704, nit.Lcns[1].Lcn);
    True(!nit.Lcns[1].Visible, "second service is flagged hidden");
});

Test("EIT event times, text and genre", () =>
{
    var start = new DateTime(2026, 11, 15, 19, 30, 0, DateTimeKind.Utc);
    byte[] title = Encoding.ASCII.GetBytes("Countryfile");
    byte[] text = Encoding.ASCII.GetBytes("Rural affairs.");
    byte[] shortEvent = Descriptor(0x4D, [(byte)'e', (byte)'n', (byte)'g', (byte)title.Length, .. title, (byte)text.Length, .. text]);
    byte[] content = Descriptor(0x54, 0x90, 0x00);
    byte[] descriptors = [.. shortEvent, .. content];
    byte[] body =
    [
        0x10, 0x41, 0x23, 0x3A, 0x00, 0x4E,
        0x12, 0x34, .. EitSection.EncodeTime(start), 0x01, 0x05, 0x00,
        (byte)(0x80 | (descriptors.Length >> 8)), (byte)descriptors.Length, .. descriptors
    ];
    var eit = EitSection.Parse(PsiBuilder.BuildSection(0x4E, 4164, 1, body))!;
    Equal(4164, eit.ServiceId);
    var e = eit.Events[0];
    Equal(start, e.StartUtc);
    Equal(3900, e.DurationSeconds);
    Equal("Countryfile", e.Title);
    Equal("Rural affairs.", e.Description);
    Equal("Education / Science / Factual", e.Genre);
});

Test("DVB text: ISO 6937 accents, control codes, table selectors", () =>
{
    Equal("Café £5", DvbText.Decode([(byte)'C', (byte)'a', (byte)'f', 0xC2, (byte)'e', (byte)' ', 0xA3, (byte)'5']));
    Equal("BBC ONE", DvbText.Decode([0x86, (byte)'B', (byte)'B', (byte)'C', 0x87, (byte)' ', (byte)'O', (byte)'N', (byte)'E']));
    Equal("Zoë", DvbText.Decode([0x15, .. Encoding.UTF8.GetBytes("Zoë")]));
    Equal("Привет", DvbText.Decode([0x01, .. Encoding.GetEncoding("iso-8859-5").GetBytes("Привет")]));
    Equal("naïve", DvbText.Decode([0x10, 0x00, 0x01, .. Encoding.Latin1.GetBytes("naïve")]));
});

Console.WriteLine("Freeview HD compressed guide text");

// Encodes with the same tables: the code for each character depends on the one before it,
// and characters with no code go through the escape state as raw bytes.
static byte[] HuffmanEncode(string text, int tableNumber)
{
    var table = FreesatHuffman.GetTable(tableNumber);
    var bits = new List<bool>();

    bool Emit(byte last, byte next)
    {
        for (int j = table.Index[last]; j < table.Index[last + 1]; j++)
        {
            if (table.Entries[j].Next == next)
            {
                for (int b = 0; b < table.Entries[j].Bits; b++)
                {
                    bits.Add((table.Entries[j].Code & (0x80000000u >> b)) != 0);
                }
                return true;
            }
        }
        return false;
    }

    void Raw(byte value)
    {
        for (int b = 7; b >= 0; b--)
        {
            bits.Add(((value >> b) & 1) != 0);
        }
    }

    byte last = 0;
    byte[] bytes = Encoding.UTF8.GetBytes(text);
    for (int i = 0; i < bytes.Length; i++)
    {
        if (bytes[i] < 0x80 && Emit(last, bytes[i]))
        {
            last = bytes[i];
            continue;
        }
        if (!Emit(last, 1))
        {
            throw new Exception($"no escape code after 0x{last:x2}");
        }
        // Escape mode runs until the first 7-bit byte, which becomes the new "previous" character.
        while (true)
        {
            Raw(bytes[i]);
            if (bytes[i] < 0x80)
            {
                break;
            }
            i++;
        }
        last = bytes[i];
    }
    if (!Emit(last, 0))
    {
        if (!Emit(last, 1))
        {
            throw new Exception($"no stop or escape code after 0x{last:x2}");
        }
        Raw(0);
    }

    var result = new byte[2 + (bits.Count + 7) / 8];
    result[0] = 0x1F;
    result[1] = (byte)tableNumber;
    for (int i = 0; i < bits.Count; i++)
    {
        if (bits[i])
        {
            result[2 + i / 8] |= (byte)(0x80 >> (i % 8));
        }
    }
    return result;
}

Test("Huffman tables load with the expected shape", () =>
{
    for (int n = 1; n <= 2; n++)
    {
        var table = FreesatHuffman.GetTable(n);
        Equal(129, table.Index.Length);
        Equal(table.Entries.Length, table.Index[128]);
        True(table.Entries.Length > 1500, $"table {n} looks truncated: {table.Entries.Length}");
        True(table.Entries.All(e => e.Bits >= 1 && e.Bits <= 32 && (e.Code & ~e.Mask) == 0), "malformed entry");
    }
});

Test("compressed titles and descriptions round-trip through both tables", () =>
{
    string[] samples =
    [
        "BBC News at Six",
        "The One Show",
        "Strictly Come Dancing: The Results",
        "Scotland's Home of the Year",
        "Match of the Day 2",
        "Antiques Roadshow visits Pollok House in Glasgow, where finds include a rare £5,000 clock. [S,AD]",
        "Café culture: Zoë explores naïve art - 50% off!",
        "Q&A; #1 @ 9pm ~ \"quoted\""
    ];
    foreach (int n in new[] { 1, 2 })
    {
        foreach (string sample in samples)
        {
            byte[] encoded = HuffmanEncode(sample, n);
            Equal(sample, DvbText.Decode(encoded), $"table {n}");
        }
    }
    True(HuffmanEncode(samples[5], 2).Length < samples[5].Length, "description should compress");
});

Test("undecodable compressed text yields an empty string, not garbage", () =>
{
    Equal("", DvbText.Decode([0x1F, 0x07, 0x55, 0x66]));
    Equal("", DvbText.Decode([0x1F, 0x01]));
});

Test("EIT with a compressed title produces a usable guide entry", () =>
{
    var start = new DateTime(2026, 11, 16, 19, 0, 0, DateTimeKind.Utc);
    byte[] title = HuffmanEncode("The One Show", 1);
    byte[] text = HuffmanEncode("Live magazine show with topical reports. [S]", 2);
    byte[] shortEvent = Descriptor(0x4D, [(byte)'e', (byte)'n', (byte)'g', (byte)title.Length, .. title, (byte)text.Length, .. text]);
    byte[] body =
    [
        0x40, 0x84, 0x23, 0x3A, 0x00, 0x4E,
        0x00, 0x07, .. EitSection.EncodeTime(start), 0x00, 0x30, 0x00,
        (byte)(0x80 | (shortEvent.Length >> 8)), (byte)shortEvent.Length, .. shortEvent
    ];
    var e = EitSection.Parse(PsiBuilder.BuildSection(0x4E, 17540, 1, body))!.Events[0];
    Equal("The One Show", e.Title);
    Equal("Live magazine show with topical reports. [S]", e.Description);
    True(e.CompressedText, "should be flagged as compressed");
});

Console.WriteLine("Per-client stream");

Test("service filter emits a one-programme PAT and only that programme's PIDs", () =>
{
    var streamer = new ServiceStreamer(4164);
    int cc = 0;
    var mux = new List<byte[]>();
    byte[] pat = PsiBuilder.BuildPat(0x1041, 1, [(4164, 0x100), (6720, 0x200)]);
    for (int round = 0; round < 40; round++)
    {
        int patCc = round;
        mux.AddRange(PsiBuilder.Packetize(0, pat, ref patCc));
        int pmtCc = round;
        mux.AddRange(PsiBuilder.Packetize(0x100, PmtSection(4164, 0x101, (0x02, 0x101), (0x03, 0x102)), ref pmtCc));
        pmtCc = round;
        mux.AddRange(PsiBuilder.Packetize(0x200, PmtSection(6720, 0x201, (0x03, 0x201)), ref pmtCc));
        for (int i = 0; i < 10; i++)
        {
            mux.Add(EsPacket(0x101, cc, 0x11));
            mux.Add(EsPacket(0x102, cc, 0x22));
            mux.Add(EsPacket(0x201, cc, 0x33));
            cc++;
        }
    }
    foreach (byte[] packet in mux)
    {
        streamer.OnPacket(packet);
    }
    streamer.OnSessionEnded("done");

    var pids = new Dictionary<int, int>();
    PatTable? outputPat = null;
    var patAssembler = new SectionAssembler(0);
    while (streamer.Reader.TryRead(out byte[]? chunk))
    {
        Equal(0, chunk.Length % 188);
        for (int offset = 0; offset < chunk.Length; offset += 188)
        {
            var packet = chunk.AsSpan(offset, 188);
            Equal((byte)0x47, packet[0]);
            int pid = TsPacket.Pid(packet);
            pids[pid] = pids.GetValueOrDefault(pid) + 1;
            if (pid == 0)
            {
                patAssembler.Push(packet, (_, s) => outputPat = PatTable.Parse(s));
            }
        }
    }
    True(outputPat is not null, "no PAT in output");
    Equal(1, outputPat!.Programs.Count);
    Equal((4164, 0x100), outputPat.Programs[0]);
    True(pids.ContainsKey(0x100) && pids.ContainsKey(0x101) && pids.ContainsKey(0x102), "missing programme PIDs");
    True(!pids.ContainsKey(0x200) && !pids.ContainsKey(0x201), "other programme leaked into the stream");
    True(pids[0x101] > 350, $"video packets lost: {pids[0x101]}");
});

Console.WriteLine("Channel list and guide");

Test("scan result uses broadcast channel numbers and hides non-playable services", () =>
{
    var mux = new MuxDefinition { Name = "PSB1", FrequencyHz = 674_000_000 };
    var pmts = new Dictionary<int, PmtTable>
    {
        [4164] = PmtTable.Parse(PmtSection(4164, 0x101, (0x02, 0x101), (0x03, 0x102)))!,
        [6720] = PmtTable.Parse(PmtSection(6720, 0x201, (0x03, 0x201)))!,
        [9000] = PmtTable.Parse(PmtSection(9000, 0x1FFF, (0x0B, 0x301)))!
    };
    var sdt = new Dictionary<int, SdtService>
    {
        [4164] = new(4164, 0x01, "BBC", "BBC ONE", false),
        [6720] = new(6720, 0x02, "BBC", "BBC Radio 4", false),
        [9000] = new(9000, 0x0C, "BBC", "Red Button data", false)
    };
    var si = new SiSnapshot(new PatTable(0x1041, 1, [(4164, 0x100), (6720, 0x200), (9000, 0x300)]), pmts, 0x233A, sdt,
        true, true, "Scotland", []);
    var lcns = new Dictionary<(int, int, int), LcnEntry>
    {
        [(0x233A, 0x1041, 4164)] = new(0x233A, 0x1041, 4164, 1, true),
        [(0x233A, 0x1041, 6720)] = new(0x233A, 0x1041, 6720, 704, true)
    };
    int unnumbered = 2000;
    var services = Scanner.BuildServices(mux, si, lcns, ref unnumbered);
    Equal(1, services[0].Number);
    Equal("tv", services[0].Kind);
    True(services[0].Enabled, "BBC ONE should be enabled");
    Equal(704, services[1].Number);
    Equal("radio", services[1].Kind);
    Equal(2000, services[2].Number);
    True(!services[2].Enabled, "data service should be hidden");
    Equal("233a.1041.1044", services[0].Key);
});

Test("a TV service that is audio-only when scanned stays a visible TV channel", () =>
{
    var mux = new MuxDefinition { Name = "PSB1", FrequencyHz = 674_000_000 };
    var pmts = new Dictionary<int, PmtTable> { [4500] = PmtTable.Parse(PmtSection(4500, 0x401, (0x03, 0x401)))! };
    var sdt = new Dictionary<int, SdtService> { [4500] = new(4500, 0x01, "BBC", "BBC ALBA", false) };
    var si = new SiSnapshot(new PatTable(0x1041, 1, [(4500, 0x400)]), pmts, 0x233A, sdt, true, true, null, []);
    int unnumbered = 2000;
    var service = Scanner.BuildServices(mux, si, new Dictionary<(int, int, int), LcnEntry>(), ref unnumbered)[0];
    Equal("tv", service.Kind);
    True(service.Enabled, "should be enabled");
});

Test("HD flag follows the service type, not just the codec", () =>
{
    var mux = new MuxDefinition { Name = "Local", FrequencyHz = 546_000_000 };
    var pmts = new Dictionary<int, PmtTable>
    {
        [1] = PmtTable.Parse(PmtSection(1, 0x101, (0x1B, 0x101), (0x03, 0x102)))!,
        [2] = PmtTable.Parse(PmtSection(2, 0x201, (0x1B, 0x201), (0x03, 0x202)))!,
        [3] = PmtTable.Parse(PmtSection(3, 0x301, (0x02, 0x301), (0x03, 0x302)))!
    };
    var sdt = new Dictionary<int, SdtService>
    {
        [1] = new(1, 0x16, "", "H.264 SD channel", false),
        [2] = new(2, 0x19, "", "H.264 HD channel", false),
        [3] = new(3, 0x01, "", "MPEG-2 SD channel", false)
    };
    var si = new SiSnapshot(new PatTable(1, 1, [(1, 0x100), (2, 0x200), (3, 0x300)]), pmts, 0x233A, sdt, true, true, null, []);
    int unnumbered = 2000;
    var services = Scanner.BuildServices(mux, si, new Dictionary<(int, int, int), LcnEntry>(), ref unnumbered);
    True(!services[0].Hd, "service type 0x16 is SD");
    True(services[1].Hd, "service type 0x19 is HD");
    True(!services[2].Hd, "MPEG-2 is SD");
});

Test("favourites and logos survive a rescan without pinning the name", () =>
{
    string dir = Path.Combine(Path.GetTempPath(), "dvbh-test-" + Guid.NewGuid().ToString("n"));
    var store = new StateStore(Options.Create(new HeadendOptions { DataDir = dir }), NullLogger<StateStore>.Instance);
    var mux = new MuxDefinition { Name = "PSB1", FrequencyHz = 674_000_000 };
    ServiceRecord Scanned(string name) => new() { Key = "233a.1041.1044", MuxKey = mux.Key, Name = name, Number = 1 };
    store.MergeScan(mux, [Scanned("BBC ONE")]);
    store.Decorate("233a.1041.1044", s => { s.Favourite = true; s.LogoFile = "x.png"; });
    store.MergeScan(mux, [Scanned("BBC ONE Scot")]);
    var after = store.FindService("233a.1041.1044")!;
    True(after.Favourite, "favourite lost");
    Equal("x.png", after.LogoFile);
    Equal("BBC ONE Scot", after.Name);
    Directory.Delete(dir, true);
});

Test("logo uploads are judged by content, and batch names are matched loosely", () =>
{
    Equal(".png", DvbHeadend.Web.LogoEndpoints.DetectImage([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]));
    Equal(".jpg", DvbHeadend.Web.LogoEndpoints.DetectImage([0xFF, 0xD8, 0xFF, 0xE0]));
    True(DvbHeadend.Web.LogoEndpoints.DetectImage(Encoding.ASCII.GetBytes("<svg onload=alert(1)>")) is null, "SVG must be refused");
    True(DvbHeadend.Web.LogoEndpoints.DetectImage(Encoding.ASCII.GetBytes("<html>")) is null, "HTML must be refused");
    Equal("bbconescot", DvbHeadend.Web.LogoEndpoints.Normalise("BBC ONE Scot"));
    Equal(DvbHeadend.Web.LogoEndpoints.Normalise("bbc-one_scot"), DvbHeadend.Web.LogoEndpoints.Normalise("BBC ONE Scot"));
});

Test("signal history keeps readings per mux and honours the requested range", () =>
{
    string dir = Path.Combine(Path.GetTempPath(), "dvbh-test-" + Guid.NewGuid().ToString("n"));
    var options = Options.Create(new HeadendOptions { DataDir = dir, SignalHistoryHours = 48 });
    var history = new SignalHistory(options, NullLogger<SignalHistory>.Instance);
    var mux = new MuxDefinition { Name = "PSB1", FrequencyHz = 674_000_000 };
    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    history.Add(mux.Key, new SignalSample { T = now - 72 * 3600, Locked = true, SnrDb = 20 });
    history.Add(mux.Key, new SignalSample { T = now - 10 * 3600, Locked = true, SnrDb = 25 });
    history.Add(mux.Key, new SignalSample { T = now - 60, Locked = true, SnrDb = 27 });
    string Json(int hours) => System.Text.Json.JsonSerializer.Serialize(history.Query([mux], TimeSpan.FromHours(hours)));
    True(!Json(48).Contains("\"SnrDb\":20"), "reading older than the retention should be dropped");
    True(Json(48).Contains("\"SnrDb\":25") && Json(48).Contains("\"SnrDb\":27"), "48 h should hold both recent readings");
    True(!Json(6).Contains("\"SnrDb\":25") && Json(6).Contains("\"SnrDb\":27"), "6 h should hold only the latest");
    history.Save();
    var reloaded = new SignalHistory(options, NullLogger<SignalHistory>.Instance);
    True(System.Text.Json.JsonSerializer.Serialize(reloaded.Query([mux], TimeSpan.FromHours(48))).Contains("\"SnrDb\":27"), "history should survive a restart");
    Directory.Delete(dir, true);
});

Test("automatic rescan is due weekly, only at the configured hour", () =>
{
    var settings = new HeadendOptions { AutoScanIntervalDays = 7, AutoScanHour = 4 };
    var now = new DateTime(2026, 11, 16, 4, 5, 0, DateTimeKind.Utc);
    True(AutoScanService.IsDue(settings, null, now, 4), "never scanned: due");
    True(AutoScanService.IsDue(settings, now.AddDays(-7).AddMinutes(40), now, 4), "a week ago: due");
    True(!AutoScanService.IsDue(settings, now.AddDays(-3), now, 4), "three days ago: not due");
    True(!AutoScanService.IsDue(settings, null, now, 15), "wrong hour: not due");
    True(!AutoScanService.IsDue(new HeadendOptions { AutoScanIntervalDays = 0 }, null, now, 4), "switched off");
});

Test("guide replaces a rescheduled slot instead of keeping both", () =>
{
    string dir = Path.Combine(Path.GetTempPath(), "dvbh-test-" + Guid.NewGuid().ToString("n"));
    var epg = new EpgStore(Options.Create(new HeadendOptions { DataDir = dir }), NullLogger<EpgStore>.Instance);
    var start = DateTime.UtcNow.AddHours(1);
    epg.Ingest(new EitSection(4164, 0x1041, 0x233A, 0x50, [new EitEvent(1, start, 3600, "Old film", "", null, false)]));
    epg.Ingest(new EitSection(4164, 0x1041, 0x233A, 0x50, [new EitEvent(2, start, 1800, "News special", "", null, false)]));
    var events = epg.GetEvents("233a.1041.1044", DateTime.UtcNow, DateTime.UtcNow.AddDays(1));
    Equal(1, events.Count);
    Equal("News special", events[0].Title);
    Directory.Delete(dir, true);
});

Console.WriteLine("MQTT");

Test("packet encoding matches the MQTT 3.1.1 wire format", () =>
{
    var length = new List<byte>();
    MqttConnection.WriteRemainingLength(length, 0);
    MqttConnection.WriteRemainingLength(length, 127);
    MqttConnection.WriteRemainingLength(length, 128);
    MqttConnection.WriteRemainingLength(length, 16383);
    MqttConnection.WriteRemainingLength(length, 2097152);
    True(length.SequenceEqual(new byte[] { 0x00, 0x7F, 0x80, 0x01, 0xFF, 0x7F, 0x80, 0x80, 0x80, 0x01 }), "remaining-length varint");

    byte[] publish = MqttConnection.BuildPublish("a/b", "hi", retain: true);
    True(publish.SequenceEqual(new byte[] { 0x31, 0x07, 0x00, 0x03, (byte)'a', (byte)'/', (byte)'b', (byte)'h', (byte)'i' }), "publish");

    byte[] subscribe = MqttConnection.BuildSubscribe(1, "x/#");
    True(subscribe.SequenceEqual(new byte[] { 0x82, 0x08, 0x00, 0x01, 0x00, 0x03, (byte)'x', (byte)'/', (byte)'#', 0x00 }), "subscribe");

    byte[] connect = MqttConnection.BuildConnect("id", "u", "p", "w", "off");
    True(connect.AsSpan(0, 2).SequenceEqual(new byte[] { 0x10, (byte)(connect.Length - 2) }), "connect header");
    True(connect.AsSpan(2, 7).SequenceEqual(new byte[] { 0x00, 0x04, (byte)'M', (byte)'Q', (byte)'T', (byte)'T', 0x04 }), "protocol name and level");
    Equal((byte)0xE6, connect[9], "connect flags: user, password, will retain, will, clean session");
    Equal((byte)0x26, MqttConnection.BuildConnect("id", null, null, "w", "off")[9], "connect flags without a login");
});

Test("Home Assistant discovery uses current keys only", () =>
{
    string dir = Path.Combine(Path.GetTempPath(), "dvbh-test-" + Guid.NewGuid().ToString("n"));
    var settings = new HeadendOptions { DataDir = dir, DefaultMuxes = [new MuxDefinition { Name = "PSB1", FrequencyHz = 674_000_000 }] };
    settings.Mqtt.Host = "broker";
    var options = Options.Create(settings);
    var store = new StateStore(options, NullLogger<StateStore>.Instance);
    using var manager = new TunerManager([new FakeTuner("adapter0")], options, NullLogger<TunerManager>.Instance);
    var publisher = new MqttPublisher(manager, store, new EpgStore(options, NullLogger<EpgStore>.Instance),
        new Scanner(manager, store, NullLogger<Scanner>.Instance), new SignalHistory(options, NullLogger<SignalHistory>.Instance),
        new MqttStatus(), options, NullLogger<MqttPublisher>.Instance);
    var discovery = publisher.BuildDiscovery();
    True(discovery.Count >= 10, $"expected a full set of entities, got {discovery.Count}");
    foreach (var (topic, payload) in discovery)
    {
        using var json = System.Text.Json.JsonDocument.Parse(payload);
        var root = json.RootElement;
        string component = topic.Split('/')[1];
        True(!root.TryGetProperty("object_id", out _), $"{topic} still sends the removed object_id key");
        True(root.GetProperty("default_entity_id").GetString()!.StartsWith(component + "."), $"{topic}: default_entity_id needs the {component} domain");
        True(root.GetProperty("unique_id").GetString()!.Length > 0, "unique_id");
        Equal(component == "button", root.TryGetProperty("command_topic", out _), $"{topic}: command_topic");
        Equal(component != "button", root.TryGetProperty("state_topic", out _), $"{topic}: state_topic");
    }
    True(publisher.BuildState().Any(s => s.Topic == "dvbheadend/summary"), "summary state missing");
    Directory.Delete(dir, true);
});

Console.WriteLine("Tuner allocation");

TunerManager Manager(int count, out List<FakeTuner> fakes, int idleSeconds = 30)
{
    fakes = Enumerable.Range(0, count).Select(i => new FakeTuner($"t{i}")).ToList();
    var options = Options.Create(new HeadendOptions { TuneTimeoutSeconds = 1, IdleReleaseSeconds = idleSeconds });
    return new TunerManager(fakes, options, NullLogger<TunerManager>.Instance);
}

var muxA = new MuxDefinition { Name = "A", FrequencyHz = 674_000_000 };
var muxB = new MuxDefinition { Name = "B", FrequencyHz = 650_000_000 };
var muxC = new MuxDefinition { Name = "C", FrequencyHz = 626_000_000, System = DeliverySystem.DVBT2, PlpId = 0 };

Test("clients on the same mux share one tuner", () =>
{
    using var manager = Manager(2, out var fakes);
    using var one = manager.SubscribeAsync(muxA, Priority.Live, "one", new CountingSink(), default).Result;
    using var two = manager.SubscribeAsync(muxA, Priority.Live, "two", new CountingSink(), default).Result;
    Equal(1, fakes.Sum(f => f.TuneCount));
    True(ReferenceEquals(one.Session, two.Session), "expected a shared session");
});

Test("a second mux takes the second tuner; a third is refused at equal priority", () =>
{
    using var manager = Manager(2, out var fakes);
    using var a = manager.SubscribeAsync(muxA, Priority.Live, "a", new CountingSink(), default).Result;
    using var b = manager.SubscribeAsync(muxB, Priority.Live, "b", new CountingSink(), default).Result;
    Equal(1, fakes[0].TuneCount);
    Equal(1, fakes[1].TuneCount);
    bool refused = false;
    try
    {
        manager.SubscribeAsync(muxC, Priority.Live, "c", new CountingSink(), default).GetAwaiter().GetResult();
    }
    catch (NoTunerAvailableException)
    {
        refused = true;
    }
    True(refused, "third mux should have been refused");
});

Test("a recording evicts the lowest-priority tuner and its client is told", () =>
{
    using var manager = Manager(2, out _);
    var guideSink = new CountingSink();
    var liveSink = new CountingSink();
    using var guide = manager.SubscribeAsync(muxA, Priority.Epg, "guide", guideSink, default).Result;
    using var live = manager.SubscribeAsync(muxB, Priority.Live, "live", liveSink, default).Result;
    using var recording = manager.SubscribeAsync(muxC, Priority.Recording, "rec", new CountingSink(), default).Result;
    True(guideSink.EndReason is not null, "guide grab should have been pre-empted");
    True(liveSink.EndReason is null, "live viewer should be untouched");
    Equal("C", recording.Session!.Mux.Name);
});

Test("packets reach the sink, and stop after unsubscribing", () =>
{
    using var manager = Manager(1, out _);
    var sink = new CountingSink();
    var subscription = manager.SubscribeAsync(muxA, Priority.Live, "x", sink, default).Result;
    Thread.Sleep(300);
    True(sink.Packets > 0, "no packets delivered");
    subscription.Dispose();
    Thread.Sleep(100);
    long after = sink.Packets;
    Thread.Sleep(200);
    Equal(after, sink.Packets);
});

Test("no signal lock is reported and frees the tuner", () =>
{
    using var manager = Manager(1, out var fakes);
    fakes[0].Lock = false;
    bool failedTune = false;
    try
    {
        manager.SubscribeAsync(muxA, Priority.Live, "x", new CountingSink(), default).GetAwaiter().GetResult();
    }
    catch (TuneFailedException)
    {
        failedTune = true;
    }
    True(failedTune, "expected TuneFailedException");
    fakes[0].Lock = true;
    using var retry = manager.SubscribeAsync(muxB, Priority.Live, "y", new CountingSink(), default).Result;
    Equal("B", retry.Session!.Mux.Name);
});

Console.WriteLine();
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

internal sealed class CountingSink : IPacketSink
{
    public long Packets;
    public string? EndReason;

    public void OnPacket(ReadOnlySpan<byte> packet)
    {
        Interlocked.Increment(ref Packets);
    }

    public void OnSessionEnded(string reason)
    {
        EndReason = reason;
    }
}

internal sealed class FakeTuner(string id) : ITuner
{
    public bool Lock = true;
    public int TuneCount;

    public string Id => id;
    public string Name => id;

    public bool Tune(MuxDefinition mux, TimeSpan timeout, CancellationToken cancel)
    {
        TuneCount++;
        return Lock;
    }

    public ITsSource OpenStream() => new NullPackets();

    public TunerSignal GetSignal() => new() { Locked = Lock };

    public void Release()
    {
    }

    public void Dispose()
    {
    }

    private sealed class NullPackets : ITsSource
    {
        public int Read(Span<byte> buffer, int timeoutMs)
        {
            Thread.Sleep(10);
            buffer[..188].Fill(0xFF);
            buffer[0] = 0x47;
            buffer[1] = 0x1F;
            buffer[2] = 0xFF;
            buffer[3] = 0x10;
            return 188;
        }

        public void Dispose()
        {
        }
    }
}
