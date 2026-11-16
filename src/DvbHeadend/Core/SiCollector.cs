using DvbHeadend.Ts;

namespace DvbHeadend.Core;

/// <summary>What a scan needs to know about one mux.</summary>
public sealed record SiSnapshot(
    PatTable? Pat,
    IReadOnlyDictionary<int, PmtTable> Pmts,
    int? OriginalNetworkId,
    IReadOnlyDictionary<int, SdtService> Services,
    bool SdtComplete,
    bool NitComplete,
    string? NetworkName,
    IReadOnlyList<LcnEntry> Lcns)
{
    public bool PmtsComplete => Pat is not null && Pat.Programs.All(p => Pmts.ContainsKey(p.Program));
}

/// <summary>
/// Watches the SI PIDs of one mux: PAT, PMTs, SDT (service names), NIT (channel
/// numbers) and EIT (programme guide).
/// </summary>
public sealed class SiCollector
{
    private const int PatPid = 0x00;
    private const int NitPid = 0x10;
    private const int SdtPid = 0x11;
    private const int EitPid = 0x12;

    private readonly object _lock = new();
    private readonly SectionAssembler?[] _assemblers = new SectionAssembler?[8192];
    private readonly SectionHandler _handler;

    private PatTable? _pat;
    private readonly Dictionary<int, PmtTable> _pmts = [];
    private readonly HashSet<int> _pmtPids = [];
    private int? _onid;
    private readonly Dictionary<int, SdtService> _services = [];
    private int _sdtVersion = -1;
    private readonly HashSet<int> _sdtSections = [];
    private bool _sdtComplete;
    private int _nitVersion = -1;
    private readonly HashSet<int> _nitSections = [];
    private bool _nitComplete;
    private string? _networkName;
    private readonly Dictionary<(int, int, int), LcnEntry> _lcns = [];

    /// <summary>Raised on the mux reader thread for every valid EIT section.</summary>
    public event Action<EitSection>? EitReceived;

    public SiCollector()
    {
        _handler = OnSection;
        _assemblers[PatPid] = new SectionAssembler(PatPid);
        _assemblers[NitPid] = new SectionAssembler(NitPid);
        _assemblers[SdtPid] = new SectionAssembler(SdtPid);
        _assemblers[EitPid] = new SectionAssembler(EitPid);
    }

    public void Process(ReadOnlySpan<byte> packet)
    {
        var assembler = _assemblers[TsPacket.Pid(packet)];
        if (assembler is not null)
        {
            assembler.Push(packet, _handler);
        }
    }

    public SiSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new SiSnapshot(_pat, new Dictionary<int, PmtTable>(_pmts), _onid,
                new Dictionary<int, SdtService>(_services), _sdtComplete, _nitComplete, _networkName,
                _lcns.Values.ToList());
        }
    }

    private void OnSection(int pid, ReadOnlySpan<byte> section)
    {
        byte tableId = section[0];
        if (pid == EitPid)
        {
            if (tableId >= 0x4E && tableId <= 0x6F && EitReceived is not null)
            {
                var eit = EitSection.Parse(section);
                if (eit is not null && eit.Events.Count > 0)
                {
                    EitReceived.Invoke(eit);
                }
            }
            return;
        }

        if ((section[5] & 1) == 0)
        {
            return; // "next" table, not yet applicable
        }

        lock (_lock)
        {
            if (pid == PatPid && tableId == 0x00)
            {
                OnPat(section);
            }
            else if (pid == SdtPid && tableId == 0x42)
            {
                OnSdt(section);
            }
            else if (pid == NitPid && tableId == 0x40)
            {
                OnNit(section);
            }
            else if (tableId == 0x02 && _pmtPids.Contains(pid))
            {
                var pmt = PmtTable.Parse(section);
                if (pmt is not null)
                {
                    _pmts[pmt.ProgramNumber] = pmt;
                }
            }
        }
    }

    private void OnPat(ReadOnlySpan<byte> section)
    {
        var pat = PatTable.Parse(section);
        if (pat is null || (_pat is not null && _pat.Version == pat.Version && _pat.TransportStreamId == pat.TransportStreamId))
        {
            return;
        }
        _pat = pat;

        foreach (int old in _pmtPids)
        {
            _assemblers[old] = null;
        }
        _pmtPids.Clear();
        _pmts.Clear();
        foreach (var (_, pmtPid) in pat.Programs)
        {
            if (pmtPid > NitPid && _pmtPids.Add(pmtPid))
            {
                _assemblers[pmtPid] = new SectionAssembler(pmtPid);
            }
        }
    }

    private void OnSdt(ReadOnlySpan<byte> section)
    {
        var sdt = SdtSection.Parse(section);
        if (sdt is null)
        {
            return;
        }
        if (sdt.Version != _sdtVersion)
        {
            _sdtVersion = sdt.Version;
            _sdtSections.Clear();
            _services.Clear();
            _sdtComplete = false;
        }
        _onid = sdt.OriginalNetworkId;
        if (_sdtSections.Add(sdt.SectionNumber))
        {
            foreach (var service in sdt.Services)
            {
                _services[service.ServiceId] = service;
            }
            _sdtComplete = _sdtSections.Count == sdt.LastSectionNumber + 1;
        }
    }

    private void OnNit(ReadOnlySpan<byte> section)
    {
        var nit = NitSection.Parse(section);
        if (nit is null)
        {
            return;
        }
        if (nit.Version != _nitVersion)
        {
            _nitVersion = nit.Version;
            _nitSections.Clear();
            _lcns.Clear();
            _nitComplete = false;
        }
        _networkName = nit.NetworkName ?? _networkName;
        if (_nitSections.Add(nit.SectionNumber))
        {
            foreach (var lcn in nit.Lcns)
            {
                _lcns[(lcn.OriginalNetworkId, lcn.TransportStreamId, lcn.ServiceId)] = lcn;
            }
            _nitComplete = _nitSections.Count == nit.LastSectionNumber + 1;
        }
    }
}
