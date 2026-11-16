using System.Text.Json.Serialization;

namespace DvbHeadend.Model;

public enum DeliverySystem
{
    DVBT,
    DVBT2
}

/// <summary>One multiplex (RF channel) to tune.</summary>
public sealed record MuxDefinition
{
    public string Name { get; init; } = "";
    public DeliverySystem System { get; init; } = DeliverySystem.DVBT;
    public long FrequencyHz { get; init; }
    public int BandwidthHz { get; init; } = 8_000_000;
    /// <summary>DVB-T2 PLP id. Null = let the demodulator pick.</summary>
    public int? PlpId { get; init; }

    [JsonIgnore]
    public string Key => $"{System}:{FrequencyHz}:{(PlpId.HasValue ? PlpId.Value.ToString() : "-")}";

    public override string ToString() => $"{Name} ({FrequencyHz / 1e6:0.###} MHz {System})";
}

public sealed class StreamRecord
{
    public int Pid { get; set; }
    public int StreamType { get; set; }
    public string Codec { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Language { get; set; }
}

/// <summary>A scanned service plus the user's channel settings for it.</summary>
public sealed class ServiceRecord
{
    public string Key { get; set; } = "";
    public int Onid { get; set; }
    public int Tsid { get; set; }
    public int ServiceId { get; set; }
    public string MuxKey { get; set; } = "";
    public string MuxName { get; set; } = "";
    public string Name { get; set; } = "";
    public string Provider { get; set; } = "";
    public int ServiceType { get; set; }
    /// <summary>tv, radio or data.</summary>
    public string Kind { get; set; } = "tv";
    public bool Hd { get; set; }
    public bool Scrambled { get; set; }
    public int PmtPid { get; set; }
    public int? Lcn { get; set; }
    /// <summary>Channel number shown to clients. Defaults to the broadcast LCN.</summary>
    public int Number { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>True once the user has changed name/number/enabled; rescans then leave those alone.</summary>
    public bool UserEdited { get; set; }
    public bool Favourite { get; set; }
    /// <summary>File name of an uploaded logo under DataDir/logos, if any.</summary>
    public string? LogoFile { get; set; }
    public List<StreamRecord> Streams { get; set; } = [];
    public DateTime LastSeenUtc { get; set; }

    public static string MakeKey(int onid, int tsid, int serviceId) => $"{onid:x4}.{tsid:x4}.{serviceId:x4}";
}

public sealed class EpgEvent
{
    public string ServiceKey { get; set; } = "";
    public int EventId { get; set; }
    public DateTime StartUtc { get; set; }
    public int DurationSeconds { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Genre { get; set; }

    [JsonIgnore]
    public DateTime EndUtc => StartUtc.AddSeconds(DurationSeconds);
}

public enum RecordingStatus
{
    Scheduled,
    Recording,
    Completed,
    Failed,
    Cancelled
}

public sealed class RecordingRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..12];
    public string ServiceKey { get; set; } = "";
    public string ChannelName { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime StopUtc { get; set; }
    public RecordingStatus Status { get; set; } = RecordingStatus.Scheduled;
    public string? FilePath { get; set; }
    public long Bytes { get; set; }
    public string? Error { get; set; }
}

public sealed class FileTunerOptions
{
    public string Id { get; set; } = "file0";
    /// <summary>Frequency in Hz (as a string key) to a .ts capture replayed in a loop.</summary>
    public Dictionary<string, string> Files { get; set; } = [];
    public double BitrateMbps { get; set; } = 8;
}

public sealed class MqttOptions
{
    /// <summary>Broker host name or address. Empty turns MQTT off.</summary>
    public string Host { get; set; } = "";
    public int Port { get; set; } = 1883;
    public bool Tls { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string BaseTopic { get; set; } = "dvbheadend";
    /// <summary>Home Assistant discovery prefix. Empty skips discovery and publishes state only.</summary>
    public string DiscoveryPrefix { get; set; } = "homeassistant";
    public int PublishSeconds { get; set; } = 15;
}

public sealed class HeadendOptions
{
    public MqttOptions Mqtt { get; set; } = new();
    public string FriendlyName { get; set; } = "DvbHeadend";
    public string DataDir { get; set; } = "/var/lib/dvbheadend";
    public string RecordingsDir { get; set; } = "/var/lib/dvbheadend/recordings";
    /// <summary>Adapter numbers to use. Empty = every /dev/dvb/adapterN found.</summary>
    public List<int> Adapters { get; set; } = [];
    /// <summary>Simulated tuners replaying .ts files; for development without an aerial.</summary>
    public List<FileTunerOptions> FileTuners { get; set; } = [];
    /// <summary>Muxes loaded into the state file the first time the service starts.</summary>
    public List<MuxDefinition> DefaultMuxes { get; set; } = [];
    public int TuneTimeoutSeconds { get; set; } = 6;
    /// <summary>How long a tuner stays on a mux after the last client leaves (fast re-zap).</summary>
    public int IdleReleaseSeconds { get; set; } = 30;
    public int EpgGrabIntervalMinutes { get; set; } = 120;
    public int EpgGrabSecondsPerMux { get; set; } = 60;
    /// <summary>Seconds between signal-quality samples of each tuned mux; 0 turns the history off.</summary>
    public int SignalSampleSeconds { get; set; } = 20;
    public int SignalHistoryHours { get; set; } = 48;
    /// <summary>Automatic channel rescan interval; 0 turns it off.</summary>
    public int AutoScanIntervalDays { get; set; } = 7;
    /// <summary>Local hour (0-23) at which an automatic rescan may start.</summary>
    public int AutoScanHour { get; set; } = 4;
    public int RecordingPreRollSeconds { get; set; } = 60;
    public int RecordingPostRollSeconds { get; set; } = 180;
}
