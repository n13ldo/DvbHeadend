using System.Diagnostics;
using DvbHeadend.Model;

namespace DvbHeadend.Tuners;

/// <summary>
/// Simulated tuner: "tuning" a frequency replays a captured .ts file in a loop
/// at a fixed bitrate. Lets the whole pipeline run without an aerial.
/// </summary>
public sealed class FileTuner : ITuner
{
    private readonly FileTunerOptions _options;
    private string? _currentFile;

    public string Id => _options.Id;
    public string Name => $"File tuner {_options.Id}";

    public FileTuner(FileTunerOptions options)
    {
        _options = options;
    }

    public bool Tune(MuxDefinition mux, TimeSpan timeout, CancellationToken cancel)
    {
        if (_options.Files.TryGetValue(mux.FrequencyHz.ToString(), out string? path) && File.Exists(path))
        {
            _currentFile = path;
            return true;
        }
        _currentFile = null;
        return false;
    }

    public ITsSource OpenStream()
    {
        if (_currentFile is null)
        {
            throw new InvalidOperationException("File tuner is not tuned");
        }
        return new PacedFileSource(_currentFile, _options.BitrateMbps * 1_000_000);
    }

    public TunerSignal GetSignal() => new()
    {
        Locked = _currentFile is not null,
        HasSignal = _currentFile is not null,
        StrengthPercent = 100,
        SnrDb = 30
    };

    public void Release()
    {
        _currentFile = null;
    }

    public void Dispose()
    {
    }

    private sealed class PacedFileSource : ITsSource
    {
        private readonly FileStream _file;
        private readonly double _bytesPerSecond;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _sent;

        public PacedFileSource(string path, double bitrate)
        {
            _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
            _bytesPerSecond = bitrate / 8;
        }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            long allowed = (long)(_clock.Elapsed.TotalSeconds * _bytesPerSecond) - _sent;
            allowed -= allowed % 188;
            if (allowed < 188)
            {
                Thread.Sleep(Math.Min(timeoutMs, 5));
                return 0;
            }

            int want = (int)Math.Min(allowed, buffer.Length - buffer.Length % 188);
            int n = _file.Read(buffer[..want]);
            if (n == 0)
            {
                _file.Position = 0;
                n = _file.Read(buffer[..want]);
            }
            _sent += n;
            return n;
        }

        public void Dispose() => _file.Dispose();
    }
}
