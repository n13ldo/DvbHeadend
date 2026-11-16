using DvbHeadend.Model;
using Microsoft.Extensions.Options;

namespace DvbHeadend.Core;

/// <summary>Starts and stops scheduled recordings and writes them to disk as .ts files.</summary>
public sealed class RecordingService(TunerManager tuners, StateStore state, IOptions<HeadendOptions> options,
    ILogger<RecordingService> log) : BackgroundService
{
    private sealed class Active
    {
        public required RecordingRecord Record { get; init; }
        public required Subscription Subscription { get; init; }
        public required ServiceStreamer Streamer { get; init; }
        public required Task Writer { get; init; }
        public required CancellationTokenSource Cancel { get; init; }
    }

    private readonly Dictionary<string, Active> _active = [];
    private readonly Dictionary<string, DateTime> _retryAfter = [];
    private readonly HeadendOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await TickAsync(stop);
                await Task.Delay(2000, stop);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            foreach (var active in _active.Values.ToList())
            {
                await FinishAsync(active, RecordingStatus.Scheduled, "interrupted by shutdown");
            }
        }
    }

    private async Task TickAsync(CancellationToken stop)
    {
        var now = DateTime.UtcNow;
        var pre = TimeSpan.FromSeconds(_options.RecordingPreRollSeconds);
        var post = TimeSpan.FromSeconds(_options.RecordingPostRollSeconds);

        foreach (var active in _active.Values.ToList())
        {
            var record = active.Record;
            if (record.Status == RecordingStatus.Cancelled || state.FindRecording(record.Id) is null)
            {
                await FinishAsync(active, RecordingStatus.Cancelled, null);
            }
            else if (now >= record.StopUtc + post)
            {
                await FinishAsync(active, RecordingStatus.Completed, null);
            }
            else if (active.Writer.IsCompleted)
            {
                // Stream ended early (tuner lost). Go back to Scheduled so the next tick retries.
                await FinishAsync(active, RecordingStatus.Scheduled, active.Streamer.EndReason ?? "stream ended");
            }
            else
            {
                state.UpdateRecording(record, r => r.Bytes = active.Streamer.BytesOut);
            }
        }

        foreach (var record in state.GetRecordings().Where(r => r.Status == RecordingStatus.Scheduled))
        {
            if (_active.ContainsKey(record.Id))
            {
                continue;
            }
            if (now >= record.StopUtc + post)
            {
                state.UpdateRecording(record, r =>
                {
                    r.Status = r.FilePath is null ? RecordingStatus.Failed : RecordingStatus.Completed;
                    r.Error ??= r.FilePath is null ? "missed: no tuner or signal during the scheduled time" : null;
                });
                continue;
            }
            if (now < record.StartUtc - pre || (_retryAfter.TryGetValue(record.Id, out var retry) && now < retry))
            {
                continue;
            }
            await StartAsync(record, stop);
        }
    }

    private async Task StartAsync(RecordingRecord record, CancellationToken stop)
    {
        var service = state.FindService(record.ServiceKey);
        var mux = service is null ? null : state.FindMux(service.MuxKey);
        if (service is null || mux is null)
        {
            state.UpdateRecording(record, r =>
            {
                r.Status = RecordingStatus.Failed;
                r.Error = "channel no longer exists";
            });
            return;
        }

        var streamer = new ServiceStreamer(service.ServiceId);
        Subscription subscription;
        try
        {
            subscription = await tuners.SubscribeAsync(mux, Priority.Recording, $"recording: {record.Title}", streamer, stop);
        }
        catch (Exception ex) when (ex is NoTunerAvailableException or TuneFailedException or IOException)
        {
            _retryAfter[record.Id] = DateTime.UtcNow.AddSeconds(15);
            state.UpdateRecording(record, r => r.Error = ex.Message);
            log.LogWarning("Recording \"{Title}\" could not start (will retry): {Message}", record.Title, ex.Message);
            return;
        }

        string path = record.FilePath ?? BuildPath(record);
        if (record.FilePath is not null)
        {
            // Resuming after an interruption: keep the first part, write a new one.
            path = Path.Combine(Path.GetDirectoryName(path)!,
                $"{Path.GetFileNameWithoutExtension(path)} (resumed {DateTime.Now:HHmmss}).ts");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var cancel = new CancellationTokenSource();
        var writer = Task.Run(() => WriteAsync(streamer, path, cancel.Token));
        _active[record.Id] = new Active
        {
            Record = record,
            Subscription = subscription,
            Streamer = streamer,
            Writer = writer,
            Cancel = cancel
        };
        state.UpdateRecording(record, r =>
        {
            r.Status = RecordingStatus.Recording;
            r.FilePath = path;
            r.Error = null;
        });
        log.LogInformation("Recording \"{Title}\" from {Channel} to {Path}", record.Title, service.Name, path);
    }

    private async Task WriteAsync(ServiceStreamer streamer, string path, CancellationToken cancel)
    {
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20, useAsync: true);
        try
        {
            await foreach (byte[] chunk in streamer.Reader.ReadAllAsync(cancel))
            {
                await file.WriteAsync(chunk, cancel);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task FinishAsync(Active active, RecordingStatus status, string? error)
    {
        _active.Remove(active.Record.Id);
        active.Subscription.Dispose();
        active.Cancel.Cancel();
        try
        {
            await active.Writer;
        }
        catch (IOException ex)
        {
            status = RecordingStatus.Failed;
            error = ex.Message;
        }
        active.Cancel.Dispose();

        if (state.FindRecording(active.Record.Id) is not null)
        {
            state.UpdateRecording(active.Record, r =>
            {
                r.Status = status;
                r.Error = error;
                r.Bytes = active.Streamer.BytesOut;
            });
        }
        log.LogInformation("Recording \"{Title}\": {Status} {Error}", active.Record.Title, status, error);
    }

    private string BuildPath(RecordingRecord record)
    {
        string title = Sanitize(record.Title.Length == 0 ? "Recording" : record.Title);
        string channel = Sanitize(record.ChannelName);
        string name = $"{title} - {record.StartUtc.ToLocalTime():yyyy-MM-dd HHmm} - {channel}.ts";
        return Path.Combine(_options.RecordingsDir, title, name);
    }

    private static string Sanitize(string value)
    {
        var chars = value.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' || c == '?' || c == '*' || c == '"' ? '_' : c);
        string cleaned = new string(chars.ToArray()).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "untitled" : cleaned;
    }
}

/// <summary>
/// Keeps the guide fresh: whenever a tuner is free it visits each mux for a
/// while to collect EIT. Runs at the lowest priority, so any viewer or
/// recording takes the tuner straight back.
/// </summary>
public sealed class EpgGrabber(TunerManager tuners, StateStore state, EpgStore epg, IOptions<HeadendOptions> options,
    ILogger<EpgGrabber> log) : BackgroundService
{
    private sealed class Sink : IPacketSink
    {
        public CancellationTokenSource Ended { get; } = new();

        public void OnPacket(ReadOnlySpan<byte> packet)
        {
        }

        public void OnSessionEnded(string reason)
        {
            Ended.Cancel();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var settings = options.Value;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stop);
            while (!stop.IsCancellationRequested)
            {
                if (settings.EpgGrabIntervalMinutes > 0)
                {
                    await GrabAsync(settings.EpgGrabSecondsPerMux, stop);
                }
                epg.Prune();
                epg.Save();
                await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, settings.EpgGrabIntervalMinutes)), stop);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            epg.Save();
        }
    }

    private async Task GrabAsync(int secondsPerMux, CancellationToken stop)
    {
        int before = epg.EventCount;
        foreach (var mux in state.GetMuxes())
        {
            if (!tuners.HasIdleTuner)
            {
                continue;
            }
            var sink = new Sink();
            try
            {
                using var subscription = await tuners.SubscribeAsync(mux, Priority.Epg, "guide update", sink, stop);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, sink.Ended.Token);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(secondsPerMux), linked.Token);
                }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                {
                    // pre-empted by a viewer; move on
                }
            }
            catch (Exception ex) when (ex is NoTunerAvailableException or TuneFailedException or IOException)
            {
                log.LogDebug("Guide update skipped {Mux}: {Message}", mux, ex.Message);
            }
        }
        log.LogInformation("Guide update done: {Count} entries ({Delta:+0;-0;0})", epg.EventCount, epg.EventCount - before);
    }
}
