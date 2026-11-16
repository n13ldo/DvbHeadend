using System.Text.Json;
using DvbHeadend.Model;
using Microsoft.Extensions.Options;

namespace DvbHeadend.Core;

/// <summary>One reading of a tuned mux. Error counts are the increase since the previous reading.</summary>
public sealed class SignalSample
{
    public long T { get; set; } // Unix seconds, UTC
    public bool Locked { get; set; }
    public double? SnrDb { get; set; }
    public double? SnrPercent { get; set; }
    public double? StrengthDbm { get; set; }
    public double? StrengthPercent { get; set; }
    public long ContinuityErrors { get; set; }
    public long UncorrectedBlocks { get; set; }
}

/// <summary>
/// Rolling signal-quality history per mux. A mux is only measured while a
/// tuner is on it, so quiet muxes get a few readings every guide refresh.
/// </summary>
public sealed class SignalHistory
{
    private readonly object _lock = new();
    private readonly Dictionary<string, List<SignalSample>> _byMux = [];
    private readonly string _path;
    private readonly TimeSpan _retention;
    private readonly ILogger<SignalHistory> _log;

    public SignalHistory(IOptions<HeadendOptions> options, ILogger<SignalHistory> log)
    {
        _log = log;
        _retention = TimeSpan.FromHours(Math.Max(1, options.Value.SignalHistoryHours));
        Directory.CreateDirectory(options.Value.DataDir);
        _path = Path.Combine(options.Value.DataDir, "signal.json");
        Load();
    }

    public void Add(string muxKey, SignalSample sample)
    {
        long cutoff = DateTimeOffset.UtcNow.Add(-_retention).ToUnixTimeSeconds();
        lock (_lock)
        {
            if (!_byMux.TryGetValue(muxKey, out var samples))
            {
                samples = [];
                _byMux[muxKey] = samples;
            }
            samples.Add(sample);
            int stale = samples.FindIndex(s => s.T >= cutoff);
            if (stale > 0)
            {
                samples.RemoveRange(0, stale);
            }
        }
    }

    public object Query(IReadOnlyList<MuxDefinition> muxes, TimeSpan range)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long from = now - (long)range.TotalSeconds;
        lock (_lock)
        {
            return new
            {
                from,
                to = now,
                muxes = muxes.Select(m => new
                {
                    key = m.Key,
                    name = m.Name,
                    frequencyHz = m.FrequencyHz,
                    system = m.System.ToString(),
                    samples = _byMux.TryGetValue(m.Key, out var samples)
                        ? samples.Where(s => s.T >= from).ToList()
                        : []
                }).ToList()
            };
        }
    }

    public void Save()
    {
        Dictionary<string, List<SignalSample>> copy;
        lock (_lock)
        {
            copy = _byMux.ToDictionary(p => p.Key, p => p.Value.ToList());
        }
        try
        {
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(copy));
            File.Move(temp, _path, overwrite: true);
        }
        catch (IOException ex)
        {
            _log.LogWarning("Could not save signal history: {Message}", ex.Message);
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
            var loaded = JsonSerializer.Deserialize<Dictionary<string, List<SignalSample>>>(File.ReadAllText(_path));
            long cutoff = DateTimeOffset.UtcNow.Add(-_retention).ToUnixTimeSeconds();
            foreach (var (key, samples) in loaded ?? [])
            {
                _byMux[key] = samples.Where(s => s.T >= cutoff).ToList();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _log.LogWarning("Could not load signal history: {Message}", ex.Message);
        }
    }
}

/// <summary>Takes a reading from every tuner that is on a mux.</summary>
public sealed class SignalSampler(TunerManager tuners, SignalHistory history, IOptions<HeadendOptions> options)
    : BackgroundService
{
    private readonly Dictionary<string, (string MuxKey, long Continuity, long Uncorrected)> _previous = [];

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        int seconds = options.Value.SignalSampleSeconds;
        if (seconds <= 0)
        {
            return;
        }
        var lastSave = DateTime.UtcNow;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), stop);
                Sample();
                if (DateTime.UtcNow - lastSave > TimeSpan.FromMinutes(10))
                {
                    history.Save();
                    lastSave = DateTime.UtcNow;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            history.Save();
        }
    }

    internal void Sample()
    {
        foreach (var tuner in tuners.GetStatus())
        {
            if (tuner.Mux is null)
            {
                _previous.Remove(tuner.Id);
                continue;
            }
            // Data flowing with "no lock" means the reading collided with a retune; skip it.
            if (!tuner.Signal.Locked && tuner.BitrateMbps > 0.5)
            {
                continue;
            }

            long uncorrected = tuner.Signal.UncorrectedBlocks ?? 0;
            long continuityDelta = tuner.ContinuityErrors;
            long uncorrectedDelta = 0;
            if (_previous.TryGetValue(tuner.Id, out var before) && before.MuxKey == tuner.Mux.Key)
            {
                continuityDelta = Math.Max(0, tuner.ContinuityErrors - before.Continuity);
                uncorrectedDelta = Math.Max(0, uncorrected - before.Uncorrected);
            }
            _previous[tuner.Id] = (tuner.Mux.Key, tuner.ContinuityErrors, uncorrected);

            history.Add(tuner.Mux.Key, new SignalSample
            {
                T = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Locked = tuner.Signal.Locked,
                SnrDb = Round(tuner.Signal.SnrDb),
                SnrPercent = Round(tuner.Signal.SnrPercent),
                StrengthDbm = Round(tuner.Signal.StrengthDbm),
                StrengthPercent = Round(tuner.Signal.StrengthPercent),
                ContinuityErrors = continuityDelta,
                UncorrectedBlocks = uncorrectedDelta
            });
        }
    }

    private static double? Round(double? value) => value.HasValue ? Math.Round(value.Value, 1) : null;
}
