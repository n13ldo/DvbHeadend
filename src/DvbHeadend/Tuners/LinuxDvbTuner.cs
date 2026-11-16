using System.Diagnostics;
using DvbHeadend.Linux;
using DvbHeadend.Model;

namespace DvbHeadend.Tuners;

/// <summary>A kernel DVB adapter (/dev/dvb/adapterN).</summary>
public sealed class LinuxDvbTuner : ITuner
{
    private readonly string _adapterDir;
    private readonly ILogger _log;
    private readonly object _lock = new();
    private DvbFrontend? _frontend;

    public string Id { get; }
    public string Name { get; }

    public LinuxDvbTuner(int adapter, ILogger log)
    {
        _adapterDir = $"/dev/dvb/adapter{adapter}";
        _log = log;
        Id = $"adapter{adapter}";
        Name = Id;
        try
        {
            using var probe = DvbFrontend.Open(Path.Combine(_adapterDir, "frontend0"), readOnly: true);
            Name = $"{probe.GetName()} #{adapter}";
        }
        catch (IOException ex)
        {
            _log.LogWarning("Could not probe {Adapter}: {Message}", Id, ex.Message);
        }
    }

    public static List<int> FindAdapters()
    {
        var found = new List<int>();
        if (!Directory.Exists("/dev/dvb"))
        {
            return found;
        }
        foreach (string dir in Directory.GetDirectories("/dev/dvb", "adapter*"))
        {
            if (int.TryParse(Path.GetFileName(dir).AsSpan("adapter".Length), out int n) && File.Exists(Path.Combine(dir, "frontend0")))
            {
                found.Add(n);
            }
        }
        found.Sort();
        return found;
    }

    public bool Tune(MuxDefinition mux, TimeSpan timeout, CancellationToken cancel)
    {
        lock (_lock)
        {
            _frontend ??= DvbFrontend.Open(Path.Combine(_adapterDir, "frontend0"));
            _frontend.Tune(mux);

            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < timeout)
            {
                cancel.ThrowIfCancellationRequested();
                if ((_frontend.ReadStatus() & DvbApi.FE_HAS_LOCK) != 0)
                {
                    _log.LogInformation("{Tuner}: locked {Mux} in {Ms} ms", Id, mux, clock.ElapsedMilliseconds);
                    return true;
                }
                Thread.Sleep(50);
            }
            _log.LogWarning("{Tuner}: no lock on {Mux} after {Seconds:0.#} s", Id, mux, timeout.TotalSeconds);
            return false;
        }
    }

    public ITsSource OpenStream() => DvbTsReader.Open(_adapterDir);

    public TunerSignal GetSignal()
    {
        // Tune() holds the lock while waiting for lock-on; don't stall status queries behind it.
        if (!Monitor.TryEnter(_lock))
        {
            return new TunerSignal();
        }
        try
        {
            return _frontend?.ReadSignal() ?? new TunerSignal();
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    public void Release()
    {
        lock (_lock)
        {
            _frontend?.Dispose();
            _frontend = null;
        }
    }

    public void Dispose() => Release();
}
