using System.Collections.Concurrent;
using System.Text.Json;
using DvbHeadend.Model;
using DvbHeadend.Web;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace DvbHeadend.Core;

/// <summary>Builds the status object shared by GET /api/status and the live feed.</summary>
public sealed class StatusBuilder(TunerManager tuners, StateStore state, EpgStore epg, Scanner scanner,
    MqttStatus mqtt, IOptions<HeadendOptions> options)
{
    public object Build(bool includeTime) => new
    {
        mqtt = new { mqtt.Enabled, mqtt.Connected, mqtt.Broker, mqtt.LastError, mqtt.LastPublishUtc },
        name = options.Value.FriendlyName,
        version = typeof(StatusBuilder).Assembly.GetName().Version?.ToString(3),
        timeUtc = includeTime ? DateTime.UtcNow : (DateTime?)null,
        tuners = tuners.GetStatus(),
        muxes = state.GetMuxes().Count,
        channels = state.GetChannels().Count,
        guideEntries = epg.EventCount,
        guideEntriesUndecodable = epg.CompressedEventsSkipped,
        scan = scanner.Status,
        lastScanUtc = state.LastScanUtc,
        autoScanIntervalDays = options.Value.AutoScanIntervalDays
    };
}

/// <summary>
/// Pushes changes to SignalR clients so pages never have to poll. Changes are
/// gathered and sent in small batches: a guide refresh can touch thousands of
/// entries a minute, and clients only need to know which channels moved.
/// </summary>
public sealed class LiveNotifier : BackgroundService
{
    private readonly IHubContext<LiveHub, ILiveClient> _hub;
    private readonly LiveClients _clients;
    private readonly StatusBuilder _status;
    private readonly StateStore _state;
    private readonly EpgStore _epg;
    private readonly ILogger<LiveNotifier> _log;
    private readonly JsonSerializerOptions _json;
    private readonly ConcurrentDictionary<string, byte> _guideDirty = new();
    private readonly Dictionary<string, (int Now, int Next)> _nowNext = [];
    private int _channelsDirty;
    private int _recordingsDirty;
    private string _lastStatus = "";

    public LiveNotifier(IHubContext<LiveHub, ILiveClient> hub, LiveClients clients, StatusBuilder status,
        StateStore state, EpgStore epg, IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json, ILogger<LiveNotifier> log)
    {
        _hub = hub;
        _clients = clients;
        _status = status;
        _state = state;
        _epg = epg;
        _log = log;
        _json = json.Value.SerializerOptions;
        epg.ServiceChanged += key => _guideDirty[key] = 0;
        state.ChannelsChanged += () => Interlocked.Exchange(ref _channelsDirty, 1);
        state.RecordingsChanged += () => Interlocked.Exchange(ref _recordingsDirty, 1);
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        long tick = 0;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(1000, stop);
                tick++;
                try
                {
                    await TickAsync(tick);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning("Live update failed: {Message}", ex.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task TickAsync(long tick)
    {
        if (_clients.Count == 0)
        {
            // Nobody listening: forget what was pending. A client reads everything afresh when it connects.
            _guideDirty.Clear();
            _nowNext.Clear();
            _lastStatus = "";
            Interlocked.Exchange(ref _channelsDirty, 0);
            Interlocked.Exchange(ref _recordingsDirty, 0);
            return;
        }

        if (Interlocked.Exchange(ref _channelsDirty, 0) == 1)
        {
            _nowNext.Clear();
            await _hub.Clients.All.ChannelsChanged();
        }

        if (tick % 2 == 0)
        {
            if (Interlocked.Exchange(ref _recordingsDirty, 0) == 1)
            {
                await _hub.Clients.All.RecordingsChanged();
            }

            string status = JsonSerializer.Serialize(_status.Build(includeTime: false), _json);
            if (status != _lastStatus)
            {
                _lastStatus = status;
                await _hub.Clients.All.Status(_status.Build(includeTime: true));
            }
        }

        if (tick % 3 == 0)
        {
            if (!_guideDirty.IsEmpty)
            {
                var keys = _guideDirty.Keys.ToList();
                foreach (string key in keys)
                {
                    _guideDirty.TryRemove(key, out _);
                }
                await _hub.Clients.All.GuideChanged(keys);
            }

            var changes = CollectNowNextChanges(_state.GetServices().Select(s => s.Key), _epg, _nowNext);
            if (changes.Count > 0)
            {
                await _hub.Clients.All.NowNext(changes);
            }
        }
    }

    /// <summary>
    /// Compares each channel's current and next programme with what was last sent.
    /// The first sighting of a channel only records it: a client that has just
    /// connected already has now/next from its own read of the channel list.
    /// </summary>
    internal static List<NowNextUpdate> CollectNowNextChanges(IEnumerable<string> keys, EpgStore epg,
        Dictionary<string, (int Now, int Next)> known)
    {
        var changes = new List<NowNextUpdate>();
        foreach (string key in keys)
        {
            var (now, next) = epg.NowNext(key);
            var signature = (Signature(now), Signature(next));
            bool seen = known.TryGetValue(key, out var previous);
            known[key] = signature;
            if (seen && previous != signature)
            {
                changes.Add(new NowNextUpdate(key, now, next));
            }
        }
        return changes;
    }

    private static int Signature(EpgEvent? e) =>
        e is null ? 0 : HashCode.Combine(e.EventId, e.StartUtc, e.DurationSeconds, e.Title);
}
