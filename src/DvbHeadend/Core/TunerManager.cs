using DvbHeadend.Model;
using DvbHeadend.Tuners;
using Microsoft.Extensions.Options;

namespace DvbHeadend.Core;

public static class Priority
{
    public const int Epg = 1;
    public const int Scan = 3;
    public const int Live = 5;
    public const int Recording = 10;
}

public sealed class NoTunerAvailableException(string message) : Exception(message);

public sealed class TuneFailedException(string message) : Exception(message);

/// <summary>A claim on a tuned mux. Dispose to release it.</summary>
public sealed class Subscription : IDisposable
{
    private readonly TunerManager _manager;
    private int _disposed;

    internal Subscription(TunerManager manager, TunerManager.Slot slot, MuxDefinition mux, int priority,
        string description, IPacketSink sink)
    {
        _manager = manager;
        Slot = slot;
        Mux = mux;
        Priority = priority;
        Description = description;
        Sink = sink;
    }

    internal TunerManager.Slot Slot { get; }
    public MuxDefinition Mux { get; }
    public int Priority { get; }
    public string Description { get; }
    public IPacketSink Sink { get; }
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public MuxSession? Session { get; internal set; }
    /// <summary>Set when a higher-priority request took the tuner away.</summary>
    public bool Preempted { get; internal set; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _manager.Unsubscribe(this);
        }
    }
}

public sealed record TunerStatus(string Id, string Name, MuxDefinition? Mux, TunerSignal Signal, double BitrateMbps,
    long ContinuityErrors, long TransportErrors, IReadOnlyList<SubscriberStatus> Subscribers);

public sealed record SubscriberStatus(string Description, int Priority, DateTime StartedUtc);

/// <summary>
/// Owns the tuners and decides who gets them. Requests for a mux that is
/// already tuned share that tuner (a dual tuner can therefore serve any number
/// of channels as long as they sit on at most two muxes). When every tuner is
/// busy, a higher-priority request evicts the lowest-priority tuner.
/// </summary>
public sealed class TunerManager : IDisposable
{
    internal sealed class Slot(ITuner tuner)
    {
        public ITuner Tuner { get; } = tuner;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public List<Subscription> Subscriptions { get; } = [];
        public MuxDefinition? Target { get; set; }
        public MuxSession? Session { get; set; }
        public DateTime IdleSinceUtc { get; set; } = DateTime.UtcNow;
    }

    private readonly object _lock = new();
    private readonly List<Slot> _slots;
    private readonly HeadendOptions _options;
    private readonly ILogger<TunerManager> _log;
    private readonly Timer _housekeeping;

    /// <summary>Raised when a tuner starts on a mux; used to hook the EPG store up to its EIT.</summary>
    public event Action<MuxSession>? SessionStarted;

    public int TunerCount => _slots.Count;

    public TunerManager(IEnumerable<ITuner> tuners, IOptions<HeadendOptions> options, ILogger<TunerManager> log)
    {
        _slots = tuners.Select(t => new Slot(t)).ToList();
        _options = options.Value;
        _log = log;
        _housekeeping = new Timer(_ => ReleaseIdle(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public bool HasIdleTuner
    {
        get
        {
            lock (_lock)
            {
                return _slots.Any(s => s.Subscriptions.Count == 0);
            }
        }
    }

    /// <summary>True when nothing at all is using a tuner (guide refresh included).</summary>
    public bool AllTunersIdle
    {
        get
        {
            lock (_lock)
            {
                return _slots.Count > 0 && _slots.All(s => s.Subscriptions.Count == 0);
            }
        }
    }

    public async Task<Subscription> SubscribeAsync(MuxDefinition mux, int priority, string description,
        IPacketSink sink, CancellationToken cancel)
    {
        Slot slot;
        Subscription subscription;
        List<Subscription> evicted = [];

        lock (_lock)
        {
            if (_slots.Count == 0)
            {
                throw new NoTunerAvailableException("No tuners are configured");
            }

            // 1. a tuner already on (or heading to) this mux; 2. an idle tuner; 3. evict.
            var candidate = _slots.FirstOrDefault(s => s.Target?.Key == mux.Key && s.Session?.Faulted != true)
                ?? _slots.Where(s => s.Subscriptions.Count == 0).OrderBy(s => s.Target is null ? 0 : 1).FirstOrDefault();
            if (candidate is null)
            {
                candidate = _slots
                    .Where(s => s.Subscriptions.Max(x => x.Priority) < priority)
                    .OrderBy(s => s.Subscriptions.Max(x => x.Priority))
                    .FirstOrDefault();
                if (candidate is null)
                {
                    throw new NoTunerAvailableException(
                        $"All {_slots.Count} tuners are busy on other muxes with equal or higher priority");
                }
                evicted.AddRange(candidate.Subscriptions);
                candidate.Subscriptions.Clear();
                foreach (var victim in evicted)
                {
                    victim.Preempted = true;
                }
            }

            slot = candidate;
            slot.Target = mux;
            subscription = new Subscription(this, slot, mux, priority, description, sink);
            slot.Subscriptions.Add(subscription);
        }

        foreach (var victim in evicted)
        {
            _log.LogInformation("{Tuner}: \"{Victim}\" pre-empted by \"{Description}\"", slot.Tuner.Id,
                victim.Description, description);
            victim.Session?.RemoveSink(victim.Sink);
            victim.Sink.OnSessionEnded("pre-empted by a higher priority request");
        }

        try
        {
            await slot.Gate.WaitAsync(cancel);
        }
        catch
        {
            Unsubscribe(subscription);
            throw;
        }

        try
        {
            if (subscription.Preempted)
            {
                throw new NoTunerAvailableException("Tuner was taken by a higher priority request");
            }

            if (slot.Session is null || slot.Session.Mux.Key != mux.Key || slot.Session.Faulted)
            {
                slot.Session?.Dispose();
                slot.Session = null;

                var timeout = TimeSpan.FromSeconds(_options.TuneTimeoutSeconds);
                bool locked = await Task.Run(() => slot.Tuner.Tune(mux, timeout, cancel), cancel);
                if (!locked)
                {
                    throw new TuneFailedException($"No signal lock on {mux}");
                }

                var session = new MuxSession(slot.Tuner, mux, _log);
                SessionStarted?.Invoke(session);
                session.Start();
                slot.Session = session;
                _log.LogInformation("{Tuner}: streaming {Mux}", slot.Tuner.Id, mux);
            }

            subscription.Session = slot.Session;
            slot.Session.AddSink(sink);
            return subscription;
        }
        catch
        {
            Unsubscribe(subscription);
            lock (_lock)
            {
                if (slot.Subscriptions.Count == 0 && slot.Session is null)
                {
                    slot.Target = null;
                    slot.Tuner.Release();
                }
            }
            throw;
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    internal void Unsubscribe(Subscription subscription)
    {
        lock (_lock)
        {
            var slot = subscription.Slot;
            if (slot.Subscriptions.Remove(subscription) && slot.Subscriptions.Count == 0)
            {
                slot.IdleSinceUtc = DateTime.UtcNow;
            }
        }
        subscription.Session?.RemoveSink(subscription.Sink);
    }

    /// <summary>Powers down tuners nobody has used for a while.</summary>
    private void ReleaseIdle()
    {
        foreach (var slot in _slots)
        {
            bool idle;
            lock (_lock)
            {
                idle = slot.Session is not null && slot.Subscriptions.Count == 0 &&
                    (DateTime.UtcNow - slot.IdleSinceUtc).TotalSeconds >= _options.IdleReleaseSeconds;
            }
            if (!idle || !slot.Gate.Wait(0))
            {
                continue;
            }
            try
            {
                MuxSession? session = null;
                lock (_lock)
                {
                    if (slot.Subscriptions.Count == 0)
                    {
                        session = slot.Session;
                        slot.Session = null;
                        slot.Target = null;
                    }
                }
                if (session is not null)
                {
                    session.Dispose();
                    slot.Tuner.Release();
                    _log.LogInformation("{Tuner}: idle, released", slot.Tuner.Id);
                }
            }
            finally
            {
                slot.Gate.Release();
            }
        }
    }

    public List<TunerStatus> GetStatus()
    {
        var result = new List<TunerStatus>();
        foreach (var slot in _slots)
        {
            MuxSession? session;
            List<SubscriberStatus> subscribers;
            lock (_lock)
            {
                session = slot.Session;
                subscribers = slot.Subscriptions
                    .Select(s => new SubscriberStatus(s.Description, s.Priority, s.StartedUtc)).ToList();
            }
            var signal = session is null ? new TunerSignal() : slot.Tuner.GetSignal();
            result.Add(new TunerStatus(slot.Tuner.Id, slot.Tuner.Name, session?.Mux, signal,
                Math.Round(session?.BitrateMbps ?? 0, 2), session?.ContinuityErrors ?? 0,
                session?.TransportErrors ?? 0, subscribers));
        }
        return result;
    }

    public void Dispose()
    {
        _housekeeping.Dispose();
        foreach (var slot in _slots)
        {
            slot.Session?.Dispose();
            slot.Tuner.Dispose();
        }
    }
}
