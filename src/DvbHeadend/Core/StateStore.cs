using System.Text.Json;
using System.Text.Json.Serialization;
using DvbHeadend.Model;
using Microsoft.Extensions.Options;

namespace DvbHeadend.Core;

/// <summary>Muxes, channels and recordings, persisted as one JSON file.</summary>
public sealed class StateStore
{
    private sealed class StateFile
    {
        public List<MuxDefinition> Muxes { get; set; } = [];
        public List<ServiceRecord> Services { get; set; } = [];
        public List<RecordingRecord> Recordings { get; set; } = [];
        public DateTime? LastScanUtc { get; set; }
    }

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _lock = new();
    private readonly string _path;
    private readonly ILogger<StateStore> _log;
    private StateFile _state = new();

    public StateStore(IOptions<HeadendOptions> options, ILogger<StateStore> log)
    {
        _log = log;
        Directory.CreateDirectory(options.Value.DataDir);
        _path = Path.Combine(options.Value.DataDir, "state.json");
        if (File.Exists(_path))
        {
            try
            {
                _state = JsonSerializer.Deserialize<StateFile>(File.ReadAllText(_path), Json) ?? new StateFile();
            }
            catch (JsonException ex)
            {
                _log.LogError(ex, "Could not read {Path}; starting with empty state", _path);
            }
        }
        if (_state.Muxes.Count == 0 && options.Value.DefaultMuxes.Count > 0)
        {
            _state.Muxes = [.. options.Value.DefaultMuxes];
            Save();
        }
        // Anything "Recording" at start-up was cut off by a restart.
        foreach (var recording in _state.Recordings.Where(r => r.Status == RecordingStatus.Recording))
        {
            recording.Status = RecordingStatus.Scheduled;
        }
    }

    private void Save()
    {
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_state, Json));
        File.Move(temp, _path, overwrite: true);
    }

    // ---- scan bookkeeping ----

    public DateTime? LastScanUtc
    {
        get
        {
            lock (_lock)
            {
                return _state.LastScanUtc;
            }
        }
    }

    public void MarkScanned()
    {
        lock (_lock)
        {
            _state.LastScanUtc = DateTime.UtcNow;
            Save();
        }
    }

    // ---- muxes ----

    public List<MuxDefinition> GetMuxes()
    {
        lock (_lock)
        {
            return [.. _state.Muxes];
        }
    }

    public MuxDefinition? FindMux(string key)
    {
        lock (_lock)
        {
            return _state.Muxes.FirstOrDefault(m => m.Key == key);
        }
    }

    public bool AddMux(MuxDefinition mux)
    {
        lock (_lock)
        {
            if (_state.Muxes.Any(m => m.Key == mux.Key))
            {
                return false;
            }
            _state.Muxes.Add(mux);
            Save();
            return true;
        }
    }

    public bool RemoveMux(string key)
    {
        lock (_lock)
        {
            if (_state.Muxes.RemoveAll(m => m.Key == key) == 0)
            {
                return false;
            }
            _state.Services.RemoveAll(s => s.MuxKey == key);
            Save();
            return true;
        }
    }

    // ---- services / channels ----

    public List<ServiceRecord> GetServices()
    {
        lock (_lock)
        {
            return [.. _state.Services.OrderBy(s => s.Number).ThenBy(s => s.Name)];
        }
    }

    public List<ServiceRecord> GetChannels() => GetServices().Where(s => s.Enabled).ToList();

    public ServiceRecord? FindService(string key)
    {
        lock (_lock)
        {
            return _state.Services.FirstOrDefault(s => s.Key == key);
        }
    }

    public ServiceRecord? FindChannelByNumber(int number)
    {
        lock (_lock)
        {
            return _state.Services.FirstOrDefault(s => s.Enabled && s.Number == number);
        }
    }

    public bool UpdateService(string key, Action<ServiceRecord> change)
    {
        lock (_lock)
        {
            var service = _state.Services.FirstOrDefault(s => s.Key == key);
            if (service is null)
            {
                return false;
            }
            change(service);
            service.UserEdited = true;
            Save();
            return true;
        }
    }

    /// <summary>Favourite and logo changes; unlike UpdateService these do not pin the name or number.</summary>
    public bool Decorate(string key, Action<ServiceRecord> change)
    {
        lock (_lock)
        {
            var service = _state.Services.FirstOrDefault(s => s.Key == key);
            if (service is null)
            {
                return false;
            }
            change(service);
            Save();
            return true;
        }
    }

    /// <summary>
    /// Folds a scan of one mux into the channel list. New services are added,
    /// known ones refreshed; name/number/enabled survive if the user changed them.
    /// </summary>
    public (int Added, int Updated) MergeScan(MuxDefinition mux, IEnumerable<ServiceRecord> found)
    {
        int added = 0;
        int updated = 0;
        lock (_lock)
        {
            var seen = new HashSet<string>();
            foreach (var scanned in found)
            {
                seen.Add(scanned.Key);
                var existing = _state.Services.FirstOrDefault(s => s.Key == scanned.Key);
                if (existing is null)
                {
                    _state.Services.Add(scanned);
                    added++;
                    continue;
                }
                // Things only the user sets always survive a rescan.
                scanned.Favourite = existing.Favourite;
                scanned.LogoFile = existing.LogoFile;
                if (existing.UserEdited)
                {
                    scanned.Name = existing.Name;
                    scanned.Number = existing.Number;
                    scanned.Enabled = existing.Enabled;
                    scanned.UserEdited = true;
                }
                _state.Services[_state.Services.IndexOf(existing)] = scanned;
                updated++;
            }
            // Services that have left this mux.
            _state.Services.RemoveAll(s => s.MuxKey == mux.Key && !seen.Contains(s.Key));
            Save();
        }
        return (added, updated);
    }

    // ---- recordings ----

    public List<RecordingRecord> GetRecordings()
    {
        lock (_lock)
        {
            return [.. _state.Recordings.OrderBy(r => r.StartUtc)];
        }
    }

    public RecordingRecord? FindRecording(string id)
    {
        lock (_lock)
        {
            return _state.Recordings.FirstOrDefault(r => r.Id == id);
        }
    }

    public void AddRecording(RecordingRecord recording)
    {
        lock (_lock)
        {
            _state.Recordings.Add(recording);
            Save();
        }
    }

    public void UpdateRecording(RecordingRecord recording, Action<RecordingRecord> change)
    {
        lock (_lock)
        {
            change(recording);
            Save();
        }
    }

    public bool RemoveRecording(string id)
    {
        lock (_lock)
        {
            bool removed = _state.Recordings.RemoveAll(r => r.Id == id) > 0;
            if (removed)
            {
                Save();
            }
            return removed;
        }
    }
}
