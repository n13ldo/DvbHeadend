using System.Text.Json;
using DvbHeadend.Model;
using DvbHeadend.Ts;
using Microsoft.Extensions.Options;

namespace DvbHeadend.Core;

/// <summary>Programme guide built from broadcast EIT, kept in memory and saved periodically.</summary>
public sealed class EpgStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Dictionary<int, EpgEvent>> _byService = [];
    private readonly string _path;
    private readonly ILogger<EpgStore> _log;
    private bool _dirty;

    public long CompressedEventsSkipped { get; private set; }

    public EpgStore(IOptions<HeadendOptions> options, ILogger<EpgStore> log)
    {
        _log = log;
        Directory.CreateDirectory(options.Value.DataDir);
        _path = Path.Combine(options.Value.DataDir, "epg.json");
        Load();
    }

    public int EventCount
    {
        get
        {
            lock (_lock)
            {
                return _byService.Values.Sum(v => v.Count);
            }
        }
    }

    /// <summary>Called on mux reader threads.</summary>
    public void Ingest(EitSection eit)
    {
        string key = ServiceRecord.MakeKey(eit.OriginalNetworkId, eit.TransportStreamId, eit.ServiceId);
        var cutoff = DateTime.UtcNow.AddHours(-2);
        lock (_lock)
        {
            if (!_byService.TryGetValue(key, out var events))
            {
                events = [];
                _byService[key] = events;
            }

            foreach (var incoming in eit.Events)
            {
                var end = incoming.StartUtc.AddSeconds(incoming.DurationSeconds);
                if (end < cutoff)
                {
                    continue;
                }
                if (incoming.Title.Length == 0)
                {
                    if (incoming.CompressedText)
                    {
                        CompressedEventsSkipped++;
                    }
                    continue;
                }
                if (events.TryGetValue(incoming.EventId, out var known) && known.StartUtc == incoming.StartUtc &&
                    known.DurationSeconds == incoming.DurationSeconds && known.Title == incoming.Title &&
                    known.Description == incoming.Description)
                {
                    continue;
                }

                // A rescheduled slot arrives under a new event id; drop whatever it overlaps.
                List<int>? stale = null;
                foreach (var other in events.Values)
                {
                    if (other.EventId != incoming.EventId && other.StartUtc < end && other.EndUtc > incoming.StartUtc)
                    {
                        (stale ??= []).Add(other.EventId);
                    }
                }
                if (stale is not null)
                {
                    foreach (int id in stale)
                    {
                        events.Remove(id);
                    }
                }

                events[incoming.EventId] = new EpgEvent
                {
                    ServiceKey = key,
                    EventId = incoming.EventId,
                    StartUtc = incoming.StartUtc,
                    DurationSeconds = incoming.DurationSeconds,
                    Title = incoming.Title,
                    Description = incoming.Description,
                    Genre = incoming.Genre
                };
                _dirty = true;
            }
        }
    }

    public List<EpgEvent> GetEvents(string serviceKey, DateTime fromUtc, DateTime toUtc)
    {
        lock (_lock)
        {
            if (!_byService.TryGetValue(serviceKey, out var events))
            {
                return [];
            }
            return events.Values.Where(e => e.EndUtc > fromUtc && e.StartUtc < toUtc).OrderBy(e => e.StartUtc).ToList();
        }
    }

    public (EpgEvent? Now, EpgEvent? Next) NowNext(string serviceKey)
    {
        var now = DateTime.UtcNow;
        var upcoming = GetEvents(serviceKey, now, now.AddHours(12));
        var current = upcoming.FirstOrDefault(e => e.StartUtc <= now);
        var next = upcoming.FirstOrDefault(e => e.StartUtc > now);
        return (current, next);
    }

    public EpgEvent? Find(string serviceKey, int eventId)
    {
        lock (_lock)
        {
            return _byService.TryGetValue(serviceKey, out var events) && events.TryGetValue(eventId, out var e) ? e : null;
        }
    }

    public void Prune()
    {
        var cutoff = DateTime.UtcNow.AddHours(-2);
        lock (_lock)
        {
            foreach (var events in _byService.Values)
            {
                foreach (int id in events.Values.Where(e => e.EndUtc < cutoff).Select(e => e.EventId).ToList())
                {
                    events.Remove(id);
                    _dirty = true;
                }
            }
        }
    }

    public void Save()
    {
        List<EpgEvent> all;
        lock (_lock)
        {
            if (!_dirty)
            {
                return;
            }
            all = _byService.Values.SelectMany(v => v.Values).ToList();
            _dirty = false;
        }
        try
        {
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(all));
            File.Move(temp, _path, overwrite: true);
        }
        catch (IOException ex)
        {
            _log.LogWarning("Could not save EPG: {Message}", ex.Message);
        }
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }
        try
        {
            var all = JsonSerializer.Deserialize<List<EpgEvent>>(File.ReadAllText(_path)) ?? [];
            var cutoff = DateTime.UtcNow.AddHours(-2);
            foreach (var e in all.Where(e => e.EndUtc >= cutoff))
            {
                if (!_byService.TryGetValue(e.ServiceKey, out var events))
                {
                    events = [];
                    _byService[e.ServiceKey] = events;
                }
                events[e.EventId] = e;
            }
            _log.LogInformation("Loaded {Count} guide entries", EventCount);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _log.LogWarning("Could not load saved EPG: {Message}", ex.Message);
        }
    }
}
