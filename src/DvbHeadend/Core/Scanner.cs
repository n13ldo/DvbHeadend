using System.Diagnostics;
using DvbHeadend.Model;
using DvbHeadend.Ts;

namespace DvbHeadend.Core;

public sealed class ScanStatus
{
    public bool Running { get; set; }
    public int MuxesTotal { get; set; }
    public int MuxesDone { get; set; }
    public string? CurrentMux { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public List<string> Log { get; set; } = [];
}

/// <summary>Tunes each configured mux in turn and builds the channel list from its SI.</summary>
public sealed class Scanner(TunerManager tuners, StateStore state, ILogger<Scanner> log)
{
    private sealed class NullSink : IPacketSink
    {
        public volatile bool Ended;

        public void OnPacket(ReadOnlySpan<byte> packet)
        {
        }

        public void OnSessionEnded(string reason)
        {
            Ended = true;
        }
    }

    private readonly object _lock = new();
    private ScanStatus _status = new();

    public ScanStatus Status
    {
        get
        {
            lock (_lock)
            {
                return new ScanStatus
                {
                    Running = _status.Running,
                    MuxesTotal = _status.MuxesTotal,
                    MuxesDone = _status.MuxesDone,
                    CurrentMux = _status.CurrentMux,
                    StartedUtc = _status.StartedUtc,
                    FinishedUtc = _status.FinishedUtc,
                    Log = [.. _status.Log]
                };
            }
        }
    }

    public bool Start()
    {
        lock (_lock)
        {
            if (_status.Running)
            {
                return false;
            }
            _status = new ScanStatus { Running = true, StartedUtc = DateTime.UtcNow };
        }
        _ = Task.Run(() => RunAsync(CancellationToken.None));
        return true;
    }

    private void Report(string line)
    {
        log.LogInformation("Scan: {Line}", line);
        lock (_lock)
        {
            _status.Log.Add(line);
        }
    }

    public async Task RunAsync(CancellationToken cancel)
    {
        var muxes = state.GetMuxes();
        lock (_lock)
        {
            _status.MuxesTotal = muxes.Count;
        }

        var perMux = new List<(MuxDefinition Mux, SiSnapshot Si)>();
        var lcns = new Dictionary<(int, int, int), LcnEntry>();
        try
        {
            foreach (var mux in muxes)
            {
                lock (_lock)
                {
                    _status.CurrentMux = mux.ToString();
                }
                try
                {
                    var si = await ScanMuxAsync(mux, cancel);
                    if (si is null)
                    {
                        Report($"{mux}: locked but no service information received");
                    }
                    else
                    {
                        perMux.Add((mux, si));
                        foreach (var lcn in si.Lcns)
                        {
                            lcns[(lcn.OriginalNetworkId, lcn.TransportStreamId, lcn.ServiceId)] = lcn;
                        }
                        Report($"{mux}: {si.Pat!.Programs.Count} services" + (si.NitComplete ? "" : " (no channel numbers from this mux)"));
                    }
                }
                catch (TuneFailedException)
                {
                    Report($"{mux}: no signal lock");
                }
                catch (Exception ex) when (ex is NoTunerAvailableException or IOException)
                {
                    Report($"{mux}: {ex.Message}");
                }
                lock (_lock)
                {
                    _status.MuxesDone++;
                }
            }

            // Channel numbers come from the NIT, which may describe services on other muxes,
            // so they are applied only once every mux has been visited.
            int unnumbered = 2000;
            int added = 0;
            int updated = 0;
            foreach (var (mux, si) in perMux)
            {
                var result = state.MergeScan(mux, BuildServices(mux, si, lcns, ref unnumbered));
                added += result.Added;
                updated += result.Updated;
            }
            if (perMux.Count > 0)
            {
                state.MarkScanned();
            }
            Report($"Finished: {added} new, {updated} updated");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Scan failed");
            Report("Scan failed: " + ex.Message);
        }
        finally
        {
            lock (_lock)
            {
                _status.Running = false;
                _status.CurrentMux = null;
                _status.FinishedUtc = DateTime.UtcNow;
            }
        }
    }

    private async Task<SiSnapshot?> ScanMuxAsync(MuxDefinition mux, CancellationToken cancel)
    {
        var sink = new NullSink();
        using var subscription = await tuners.SubscribeAsync(mux, Priority.Scan, "scan", sink, cancel);
        var session = subscription.Session!;

        // PAT/PMT/SDT repeat within ~2 s; the NIT is allowed up to 10 s.
        var clock = Stopwatch.StartNew();
        SiSnapshot snapshot = session.Si.Snapshot();
        while (clock.Elapsed < TimeSpan.FromSeconds(15) && !sink.Ended)
        {
            await Task.Delay(250, cancel);
            snapshot = session.Si.Snapshot();
            bool core = snapshot.PmtsComplete && snapshot.SdtComplete;
            if (core && (snapshot.NitComplete || clock.Elapsed > TimeSpan.FromSeconds(11)))
            {
                break;
            }
        }
        return snapshot.Pat is null ? null : snapshot;
    }

    internal static List<ServiceRecord> BuildServices(MuxDefinition mux, SiSnapshot si,
        IReadOnlyDictionary<(int, int, int), LcnEntry> lcns, ref int unnumbered)
    {
        var services = new List<ServiceRecord>();
        int onid = si.OriginalNetworkId ?? 0;
        int tsid = si.Pat!.TransportStreamId;
        foreach (var (program, pmtPid) in si.Pat.Programs)
        {
            si.Pmts.TryGetValue(program, out var pmt);
            si.Services.TryGetValue(program, out var sdt);
            lcns.TryGetValue((onid, tsid, program), out var lcn);

            bool hasVideo = pmt?.Streams.Any(s => s.Kind == "video") == true;
            bool hasAudio = pmt?.Streams.Any(s => s.Kind == "audio") == true;
            int type = sdt?.ServiceType ?? 0;
            string kind = type switch
            {
                0x01 or 0x11 or 0x16 or 0x19 or 0x1F or 0x20 => "tv",
                0x02 or 0x0A => "radio",
                _ => hasVideo ? "tv" : hasAudio ? "radio" : "data"
            };
            // A TV service can be audio-only when scanned (BBC ALBA carries radio while off air),
            // so a declared TV type is kept and counts as playable with either stream kind.
            bool playable = kind != "data" && (hasVideo || hasAudio);
            // 0x16 is H.264 in standard definition, so the codec alone does not mean HD.
            bool hd = type is 0x11 or 0x19 or 0x1F or 0x20 ||
                (type == 0 && pmt?.Streams.Any(s => s.Codec is "h264" or "hevc") == true);

            var record = new ServiceRecord
            {
                Key = ServiceRecord.MakeKey(onid, tsid, program),
                Onid = onid,
                Tsid = tsid,
                ServiceId = program,
                MuxKey = mux.Key,
                MuxName = mux.Name,
                Name = string.IsNullOrWhiteSpace(sdt?.Name) ? $"Service {program}" : sdt!.Name,
                Provider = sdt?.Provider ?? "",
                ServiceType = type,
                Kind = kind,
                Hd = hd,
                Scrambled = pmt?.Scrambled == true,
                PmtPid = pmtPid,
                Lcn = lcn?.Lcn,
                Number = lcn is not null && lcn.Lcn > 0 ? lcn.Lcn : unnumbered++,
                Enabled = playable && pmt?.Scrambled != true && (lcn is null || lcn.Visible),
                LastSeenUtc = DateTime.UtcNow,
                Streams = pmt?.Streams.Select(s => new StreamRecord
                {
                    Pid = s.Pid,
                    StreamType = s.StreamType,
                    Codec = s.Codec,
                    Kind = s.Kind,
                    Language = s.Language
                }).ToList() ?? []
            };
            services.Add(record);
        }
        return services;
    }
}
