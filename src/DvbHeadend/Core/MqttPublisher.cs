using System.Text.Json;
using System.Text.Json.Nodes;
using DvbHeadend.Model;
using DvbHeadend.Web;
using Microsoft.Extensions.Options;

namespace DvbHeadend.Core;

/// <summary>What the web UI shows about the broker link.</summary>
public sealed class MqttStatus
{
    public bool Enabled { get; set; }
    public bool Connected { get; set; }
    public string? Broker { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastPublishUtc { get; set; }
}

/// <summary>
/// Publishes tuner, viewing and signal state to an MQTT broker, with Home
/// Assistant discovery so the entities appear on their own. Listens for one
/// command: {base}/command/scan starts a channel scan.
/// </summary>
public sealed class MqttPublisher(TunerManager tuners, StateStore state, EpgStore epg, Scanner scanner,
    SignalHistory signal, MqttStatus status, IOptions<HeadendOptions> options, ILogger<MqttPublisher> log)
    : BackgroundService
{
    private readonly HeadendOptions _headend = options.Value;
    private readonly MqttOptions _mqtt = options.Value.Mqtt;
    private readonly Dictionary<string, string> _sent = [];
    private string Base => _mqtt.BaseTopic.Trim('/');
    private string DeviceId => "dvbheadend_" + GuideEndpoints.DeviceId(_headend).ToLowerInvariant();

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (string.IsNullOrWhiteSpace(_mqtt.Host))
        {
            return;
        }
        status.Enabled = true;
        status.Broker = $"{_mqtt.Host}:{_mqtt.Port}";
        int backoff = 5;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(stop);
                backoff = 5;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException
                or System.Security.Authentication.AuthenticationException or OperationCanceledException)
            {
                if (status.LastError != ex.Message)
                {
                    log.LogWarning("MQTT: {Message}. Retrying.", ex.Message);
                }
                status.LastError = ex.Message;
            }
            status.Connected = false;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(backoff), stop);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            backoff = Math.Min(60, backoff * 2);
        }
    }

    private async Task RunSessionAsync(CancellationToken stop)
    {
        string availability = $"{Base}/status";
        await using var connection = await MqttConnection.ConnectAsync(_mqtt, DeviceId, availability, "offline", stop);
        connection.MessageReceived = OnMessage;
        status.Connected = true;
        status.LastError = null;
        log.LogInformation("MQTT: connected to {Broker}", status.Broker);
        _sent.Clear();

        await connection.PublishAsync(availability, "online", retain: true, stop);
        await connection.SubscribeAsync($"{Base}/command/#", stop);

        var lastDiscovery = DateTime.MinValue;
        int interval = Math.Max(2, _mqtt.PublishSeconds);
        try
        {
            while (!stop.IsCancellationRequested && !connection.Completion.IsCompleted)
            {
                // Discovery is repeated so a restarted Home Assistant, or a rescan that changes the muxes, catches up.
                if (!string.IsNullOrWhiteSpace(_mqtt.DiscoveryPrefix) && DateTime.UtcNow - lastDiscovery > TimeSpan.FromMinutes(10))
                {
                    foreach (var (topic, payload) in BuildDiscovery())
                    {
                        await connection.PublishAsync(topic, payload, retain: true, stop);
                    }
                    lastDiscovery = DateTime.UtcNow;
                }

                foreach (var (topic, payload) in BuildState())
                {
                    // Unchanged values are skipped; retained copies on the broker stay correct.
                    if (!_sent.TryGetValue(topic, out string? previous) || previous != payload)
                    {
                        await connection.PublishAsync(topic, payload, retain: true, stop);
                        _sent[topic] = payload;
                    }
                }
                status.LastPublishUtc = DateTime.UtcNow;
                await connection.PingAsync(stop);
                await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(interval), stop), connection.Completion);
            }
            if (connection.Completion.IsCompleted && !stop.IsCancellationRequested)
            {
                throw new IOException("Broker closed the connection");
            }
        }
        finally
        {
            if (stop.IsCancellationRequested)
            {
                try
                {
                    using var brief = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await connection.PublishAsync(availability, "offline", retain: true, brief.Token);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                {
                }
            }
        }
    }

    private void OnMessage(string topic, string payload)
    {
        if (topic == $"{Base}/command/scan")
        {
            log.LogInformation("MQTT: channel scan requested");
            scanner.Start();
        }
    }

    // ---- state ----

    internal static string Slug(MuxDefinition mux) =>
        $"{mux.FrequencyHz / 1000}{(mux.PlpId.HasValue ? "_plp" + mux.PlpId.Value : "")}";

    internal List<(string Topic, string Payload)> BuildState()
    {
        var result = new List<(string, string)>();
        var tunerStatus = tuners.GetStatus();
        var viewing = new List<string>();

        foreach (var tuner in tunerStatus)
        {
            var live = tuner.Subscribers.Where(s => s.Priority == Priority.Live).ToList();
            var channels = live.Select(s => s.Description.Split(" → ")[0]).Distinct().ToList();
            viewing.AddRange(channels);
            var other = tuner.Subscribers.Where(s => s.Priority != Priority.Live)
                .Select(s => s.Priority == Priority.Recording ? "Recording" : s.Priority == Priority.Scan ? "Scanning" : "Guide update")
                .Distinct().ToList();

            string activity = tuner.Mux is null ? "Idle"
                : channels.Count > 0 ? string.Join(", ", channels)
                : other.Count > 0 ? string.Join(", ", other)
                : "Standby";
            result.Add(($"{Base}/tuner/{tuner.Id}/state", JsonSerializer.Serialize(new
            {
                activity,
                mux = tuner.Mux?.Name,
                frequency_mhz = tuner.Mux is null ? (double?)null : Math.Round(tuner.Mux.FrequencyHz / 1e6, 3),
                locked = tuner.Mux is not null && tuner.Signal.Locked,
                snr_db = Rounded(tuner.Signal.SnrDb),
                snr_percent = Rounded(tuner.Signal.SnrPercent),
                strength_dbm = Rounded(tuner.Signal.StrengthDbm),
                strength_percent = Rounded(tuner.Signal.StrengthPercent),
                bitrate_mbps = Math.Round(tuner.BitrateMbps, 1),
                viewers = live.Count,
                channels,
                continuity_errors = tuner.ContinuityErrors
            })));
        }

        foreach (var mux in state.GetMuxes())
        {
            var latest = signal.Latest(mux.Key);
            result.Add(($"{Base}/mux/{Slug(mux)}/state", JsonSerializer.Serialize(new
            {
                name = mux.Name,
                snr_db = latest?.SnrDb,
                snr_percent = latest?.SnrPercent,
                strength_dbm = latest?.StrengthDbm,
                strength_percent = latest?.StrengthPercent,
                locked = latest?.Locked,
                errors_last_hour = signal.ErrorsSince(mux.Key, TimeSpan.FromHours(1)),
                measured = latest is null ? null : DateTimeOffset.FromUnixTimeSeconds(latest.T).ToString("yyyy-MM-ddTHH:mm:ssZ")
            })));
        }

        var scan = scanner.Status;
        result.Add(($"{Base}/summary", JsonSerializer.Serialize(new
        {
            version = typeof(MqttPublisher).Assembly.GetName().Version?.ToString(3),
            tuners = tunerStatus.Count,
            tuners_in_use = tunerStatus.Count(t => t.Subscribers.Count > 0),
            viewers = tunerStatus.Sum(t => t.Subscribers.Count(s => s.Priority == Priority.Live)),
            watching = viewing.Count > 0 ? string.Join(", ", viewing.Distinct()) : "Nothing",
            channels = state.GetChannels().Count,
            guide_entries = epg.EventCount,
            scanning = scan.Running,
            last_scan = state.LastScanUtc?.ToString("yyyy-MM-ddTHH:mm:ssZ")
        })));
        return result;
    }

    private static double? Rounded(double? value) => value.HasValue ? Math.Round(value.Value, 1) : null;

    // ---- Home Assistant discovery ----

    internal List<(string Topic, string Payload)> BuildDiscovery()
    {
        string prefix = _mqtt.DiscoveryPrefix.Trim('/');
        string availability = $"{Base}/status";
        var device = new JsonObject
        {
            ["identifiers"] = new JsonArray(DeviceId),
            ["name"] = _headend.FriendlyName,
            ["manufacturer"] = "DvbHeadend",
            ["model"] = "DVB-T/T2 headend",
            ["sw_version"] = typeof(MqttPublisher).Assembly.GetName().Version?.ToString(3)
        };
        var result = new List<(string, string)>();

        void Entity(string component, string id, string name, string stateTopic, string? template, Action<JsonObject>? more = null)
        {
            var config = new JsonObject
            {
                ["name"] = name,
                ["unique_id"] = $"{DeviceId}_{id}",
                ["object_id"] = $"dvbheadend_{id}",
                ["availability_topic"] = availability,
                ["device"] = device.DeepClone()
            };
            if (component != "button")
            {
                config["state_topic"] = stateTopic;
                config["value_template"] = template;
            }
            more?.Invoke(config);
            result.Add(($"{prefix}/{component}/{DeviceId}/{id}/config", config.ToJsonString()));
        }

        void Measurement(JsonObject config, string unit, string? deviceClass = null)
        {
            config["unit_of_measurement"] = unit;
            config["state_class"] = "measurement";
            if (deviceClass is not null)
            {
                config["device_class"] = deviceClass;
            }
        }

        var status = tuners.GetStatus();
        // Report whichever SNR scale the hardware provides.
        bool percent = status.Any(t => t.Signal.SnrPercent.HasValue) && !status.Any(t => t.Signal.SnrDb.HasValue);
        string snrField = percent ? "snr_percent" : "snr_db";
        string snrUnit = percent ? "%" : "dB";

        int number = 1;
        foreach (var tuner in status)
        {
            string topic = $"{Base}/tuner/{tuner.Id}/state";
            string label = $"Tuner {number++}";
            Entity("sensor", $"{tuner.Id}_activity", label, topic, "{{ value_json.activity }}", c =>
            {
                c["icon"] = "mdi:television-classic";
                c["json_attributes_topic"] = topic;
            });
            Entity("sensor", $"{tuner.Id}_snr", $"{label} SNR", topic, "{{ value_json." + snrField + " }}", c =>
            {
                Measurement(c, snrUnit);
                c["icon"] = "mdi:signal";
                c["entity_category"] = "diagnostic";
            });
            Entity("sensor", $"{tuner.Id}_viewers", $"{label} viewers", topic, "{{ value_json.viewers }}", c =>
            {
                c["state_class"] = "measurement";
                c["icon"] = "mdi:account-multiple";
            });
            Entity("binary_sensor", $"{tuner.Id}_lock", $"{label} lock", topic, "{{ 'ON' if value_json.locked else 'OFF' }}", c =>
            {
                c["device_class"] = "connectivity";
                c["entity_category"] = "diagnostic";
            });
        }

        foreach (var mux in state.GetMuxes())
        {
            string topic = $"{Base}/mux/{Slug(mux)}/state";
            Entity("sensor", $"mux_{Slug(mux)}_snr", $"{mux.Name} SNR", topic, "{{ value_json." + snrField + " }}", c =>
            {
                Measurement(c, snrUnit);
                c["icon"] = "mdi:radio-tower";
                c["entity_category"] = "diagnostic";
                c["json_attributes_topic"] = topic;
            });
            Entity("sensor", $"mux_{Slug(mux)}_errors", $"{mux.Name} errors (1 h)", topic, "{{ value_json.errors_last_hour }}", c =>
            {
                c["state_class"] = "measurement";
                c["icon"] = "mdi:alert-circle-outline";
                c["entity_category"] = "diagnostic";
            });
        }

        string summary = $"{Base}/summary";
        Entity("sensor", "watching", "Watching", summary, "{{ value_json.watching }}", c =>
        {
            c["icon"] = "mdi:television-play";
            c["json_attributes_topic"] = summary;
        });
        Entity("sensor", "viewers", "Viewers", summary, "{{ value_json.viewers }}", c =>
        {
            c["state_class"] = "measurement";
            c["icon"] = "mdi:account-multiple";
        });
        Entity("sensor", "tuners_in_use", "Tuners in use", summary, "{{ value_json.tuners_in_use }}", c =>
        {
            c["state_class"] = "measurement";
            c["icon"] = "mdi:expansion-card";
        });
        Entity("sensor", "channels", "Channels", summary, "{{ value_json.channels }}", c =>
        {
            c["icon"] = "mdi:format-list-numbered";
            c["entity_category"] = "diagnostic";
        });
        Entity("sensor", "last_scan", "Last channel scan", summary, "{{ value_json.last_scan }}", c =>
        {
            c["device_class"] = "timestamp";
            c["entity_category"] = "diagnostic";
        });
        Entity("binary_sensor", "scanning", "Scanning", summary, "{{ 'ON' if value_json.scanning else 'OFF' }}", c =>
        {
            c["device_class"] = "running";
            c["entity_category"] = "diagnostic";
        });
        Entity("button", "scan", "Rescan channels", "", null, c =>
        {
            c["command_topic"] = $"{Base}/command/scan";
            c["icon"] = "mdi:radar";
            c["entity_category"] = "config";
        });
        return result;
    }
}
