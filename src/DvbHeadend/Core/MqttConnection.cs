using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using DvbHeadend.Model;

namespace DvbHeadend.Core;

/// <summary>
/// A small MQTT 3.1.1 client: connect with a last-will, publish and subscribe
/// at QoS 0, keep-alive pings. Enough for telemetry; no third-party packages.
/// </summary>
public sealed class MqttConnection : IAsyncDisposable
{
    public const int KeepAliveSeconds = 60;

    private readonly TcpClient _tcp;
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _closed = new();
    private Task _reader = Task.CompletedTask;

    /// <summary>Called for each message on a subscribed topic.</summary>
    public Action<string, string>? MessageReceived { get; set; }

    /// <summary>Completes when the broker connection ends, for whatever reason.</summary>
    public Task Completion => _reader;

    private MqttConnection(TcpClient tcp, Stream stream)
    {
        _tcp = tcp;
        _stream = stream;
    }

    public static async Task<MqttConnection> ConnectAsync(MqttOptions options, string clientId, string willTopic,
        string willPayload, CancellationToken cancel)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await tcp.ConnectAsync(options.Host, options.Port, timeout.Token);
            Stream stream = tcp.GetStream();
            if (options.Tls)
            {
                var tls = new SslStream(stream);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = options.Host }, timeout.Token);
                stream = tls;
            }

            var connection = new MqttConnection(tcp, stream);
            await stream.WriteAsync(BuildConnect(clientId, options.Username, options.Password, willTopic, willPayload), timeout.Token);
            var (type, body) = await ReadPacketAsync(stream, timeout.Token);
            if (type != 2 || body.Length < 2)
            {
                throw new IOException("Broker did not answer the connect request");
            }
            if (body[1] != 0)
            {
                throw new IOException(body[1] switch
                {
                    4 or 5 => "Broker refused the login: check the MQTT user name and password",
                    _ => $"Broker refused the connection (code {body[1]})"
                });
            }
            connection._reader = Task.Run(connection.ReadLoopAsync);
            return connection;
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new IOException($"Timed out connecting to {options.Host}:{options.Port}");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public Task PublishAsync(string topic, string payload, bool retain, CancellationToken cancel) =>
        SendAsync(BuildPublish(topic, payload, retain), cancel);

    public Task SubscribeAsync(string topicFilter, CancellationToken cancel) =>
        SendAsync(BuildSubscribe(1, topicFilter), cancel);

    public Task PingAsync(CancellationToken cancel) => SendAsync([0xC0, 0x00], cancel);

    private async Task SendAsync(byte[] packet, CancellationToken cancel)
    {
        await _writeLock.WaitAsync(cancel);
        try
        {
            await _stream.WriteAsync(packet, cancel);
            await _stream.FlushAsync(cancel);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_closed.IsCancellationRequested)
            {
                var (type, body) = await ReadPacketAsync(_stream, _closed.Token);
                if (type == 3 && body.Length >= 2)
                {
                    int topicLength = (body[0] << 8) | body[1];
                    if (2 + topicLength <= body.Length)
                    {
                        string topic = Encoding.UTF8.GetString(body, 2, topicLength);
                        string payload = Encoding.UTF8.GetString(body, 2 + topicLength, body.Length - 2 - topicLength);
                        MessageReceived?.Invoke(topic, payload);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // connection ended; the owner sees Completion finish and reconnects
        }
    }

    private static async Task<(int Type, byte[] Body)> ReadPacketAsync(Stream stream, CancellationToken cancel)
    {
        var one = new byte[1];
        await stream.ReadExactlyAsync(one, cancel);
        int type = one[0] >> 4;

        int length = 0;
        int shift = 0;
        while (true)
        {
            await stream.ReadExactlyAsync(one, cancel);
            length |= (one[0] & 0x7F) << shift;
            if ((one[0] & 0x80) == 0)
            {
                break;
            }
            shift += 7;
            if (shift > 21)
            {
                throw new IOException("Malformed packet length from broker");
            }
        }
        if (length > 1024 * 1024)
        {
            throw new IOException("Oversized packet from broker");
        }
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, cancel);
        return (type, body);
    }

    // ---- packet encoding ----

    internal static void WriteRemainingLength(List<byte> output, int length)
    {
        do
        {
            byte b = (byte)(length % 128);
            length /= 128;
            if (length > 0)
            {
                b |= 0x80;
            }
            output.Add(b);
        }
        while (length > 0);
    }

    private static void WriteString(List<byte> output, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        output.Add((byte)(bytes.Length >> 8));
        output.Add((byte)bytes.Length);
        output.AddRange(bytes);
    }

    private static byte[] Frame(byte header, List<byte> body)
    {
        var packet = new List<byte>(body.Count + 5) { header };
        WriteRemainingLength(packet, body.Count);
        packet.AddRange(body);
        return packet.ToArray();
    }

    internal static byte[] BuildConnect(string clientId, string? username, string? password, string willTopic, string willPayload)
    {
        var body = new List<byte>();
        WriteString(body, "MQTT");
        body.Add(4); // protocol level 3.1.1
        byte flags = 0x02 | 0x04 | 0x20; // clean session, will, will retain
        if (!string.IsNullOrEmpty(username))
        {
            flags |= 0x80;
            if (!string.IsNullOrEmpty(password))
            {
                flags |= 0x40;
            }
        }
        body.Add(flags);
        body.Add(KeepAliveSeconds >> 8);
        body.Add(KeepAliveSeconds & 0xFF);
        WriteString(body, clientId);
        WriteString(body, willTopic);
        WriteString(body, willPayload);
        if (!string.IsNullOrEmpty(username))
        {
            WriteString(body, username);
            if (!string.IsNullOrEmpty(password))
            {
                WriteString(body, password);
            }
        }
        return Frame(0x10, body);
    }

    internal static byte[] BuildPublish(string topic, string payload, bool retain)
    {
        var body = new List<byte>();
        WriteString(body, topic);
        body.AddRange(Encoding.UTF8.GetBytes(payload));
        return Frame((byte)(0x30 | (retain ? 0x01 : 0x00)), body);
    }

    internal static byte[] BuildSubscribe(ushort packetId, string topicFilter)
    {
        var body = new List<byte> { (byte)(packetId >> 8), (byte)packetId };
        WriteString(body, topicFilter);
        body.Add(0); // QoS 0
        return Frame(0x82, body);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var brief = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await SendAsync([0xE0, 0x00], brief.Token); // DISCONNECT: tells the broker not to send the will
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
        _closed.Cancel();
        _tcp.Dispose();
    }
}
