using MQTTnet;
using MQTTnet.Formatter;
using System.Text.Json;
using Telemetry.Core;

namespace Telemetry.Mqtt;

public sealed record MqttProfile(string Host = "127.0.0.1", int Port = 1883,
    string Topic = "emulator/+/telemetry", string SourceId = "hivemq-local",
    string ClientId = "binance-monitor-sensors", bool Tls = false, string? Username = null,
    string? FlatPressureDevice = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || Host.Contains('/') || Port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(SourceId) || string.IsNullOrWhiteSpace(ClientId) ||
            SourceId.Length > 128 || ClientId.Length > 128 || string.IsNullOrWhiteSpace(Topic) ||
            Topic.Length > 1024 || Topic.Contains('\0')) throw new ArgumentException("Invalid MQTT profile.");
        var levels = Topic.Split('/');
        for (var i = 0; i < levels.Length; i++)
            if ((levels[i].Contains('#') && (levels[i] != "#" || i != levels.Length - 1)) ||
                (levels[i].Contains('+') && levels[i] != "+")) throw new ArgumentException("Invalid MQTT topic filter.");
        if (FlatPressureDevice is not null && (string.IsNullOrWhiteSpace(FlatPressureDevice) || FlatPressureDevice.Length > 128))
            throw new ArgumentException("Flat pressure profile needs a device ID.");
    }

    public MqttClientOptions Options(string? password = null)
    {
        Validate();
        var builder = new MqttClientOptionsBuilder().WithTcpServer(Host, Port).WithClientId(ClientId)
            .WithProtocolVersion(MqttProtocolVersion.V311).WithCleanSession(false).WithTimeout(TimeSpan.FromSeconds(10));
        if (!string.IsNullOrWhiteSpace(Username)) builder.WithCredentials(Username, password);
        if (Tls) builder.WithTlsOptions(o => o.UseTls());
        return builder.Build();
    }
}

public sealed class MqttSource
{
    public event Action<string>? StatusChanged;
    public long Rejected => Interlocked.Read(ref rejected);
    private long rejected;

    // The handler must return only after durable commit. One awaited callback bounds processing.
    public async Task RunAsync(MqttProfile profile, string? password,
        Func<SensorReading, CancellationToken, Task> persist, CancellationToken token)
    {
        profile.Validate();
        var attempt = 0;
        while (!token.IsCancellationRequested)
        {
            using var client = new MqttClientFactory().CreateMqttClient();
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fatal = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var gate = new SemaphoreSlim(1);
            client.DisconnectedAsync += _ => { disconnected.TrySetResult(); return Task.CompletedTask; };
            client.ApplicationMessageReceivedAsync += async args =>
            {
                args.AutoAcknowledge = false;
                await gate.WaitAsync(CancellationToken.None);
                try
                {
                    if (token.IsCancellationRequested || fatal.Task.IsCompleted) return;
                    SensorReading reading;
                    try
                    {
                        if (args.ApplicationMessage.Payload.Length > ReadingParser.MaximumPayloadBytes)
                            throw new FormatException("Payload exceeds 64 KiB.");
                        reading = ReadingParser.Parse(args.ApplicationMessage.ConvertPayloadToString(), profile.SourceId,
                            args.ApplicationMessage.Topic, args.ApplicationMessage.Retain, profile.FlatPressureDevice);
                    }
                    catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
                    {
                        Interlocked.Increment(ref rejected);
                        // Poison messages are intentionally acknowledged and counted, never retried forever.
                        await args.AcknowledgeAsync(token);
                        return;
                    }
                    // Finish the current write even if stop is requested; database timeout bounds shutdown.
                    await persist(reading, CancellationToken.None);
                    await args.AcknowledgeAsync(CancellationToken.None);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex) { fatal.TrySetResult(ex); }
                finally { gate.Release(); }
            };
            Exception? storageFailure = null;
            try
            {
                StatusChanged?.Invoke(attempt == 0 ? "Connecting" : "Reconnecting");
                var result = await client.ConnectAsync(profile.Options(password), token);
                if (result.ResultCode is MqttClientConnectResultCode.ServerUnavailable or MqttClientConnectResultCode.ServerBusy)
                    throw new IOException($"Broker temporarily unavailable: {result.ResultCode}");
                if (result.ResultCode != MqttClientConnectResultCode.Success)
                    throw new InvalidOperationException($"Broker rejected connection: {result.ResultCode}");
                var subscription = new MqttClientFactory().CreateSubscribeOptionsBuilder()
                    .WithTopicFilter(f => f.WithTopic(profile.Topic).WithAtLeastOnceQoS()).Build();
                var subscribed = await client.SubscribeAsync(subscription, token);
                if (subscribed.Items.Any(x => (int)x.ResultCode >= 128))
                    throw new InvalidOperationException("Broker rejected subscription.");
                var connectedAt = DateTimeOffset.UtcNow;
                StatusChanged?.Invoke("Connected");
                await Task.WhenAny(disconnected.Task, fatal.Task).WaitAsync(token);
                if (DateTimeOffset.UtcNow - connectedAt >= TimeSpan.FromSeconds(30)) attempt = 0;
                if (fatal.Task.IsCompleted) storageFailure = await fatal.Task;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex) when (ex.GetBaseException() is System.Security.Authentication.AuthenticationException)
            { throw new InvalidOperationException("TLS authentication failed; check broker certificate and host name.", ex); }
            catch (Exception ex) { StatusChanged?.Invoke($"Reconnecting: {ex.GetType().Name}"); }
            finally
            {
                if (client.IsConnected)
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), deadline.Token); }
                    catch (Exception) { /* Dispose still closes transport; unacknowledged messages may redeliver. */ }
                }
                await gate.WaitAsync(CancellationToken.None);
                gate.Release();
            }
            if (fatal.Task.IsCompleted) storageFailure = await fatal.Task;
            if (storageFailure is not null) throw new IOException("Ingestion stopped: storage/processing failed.", storageFailure);
            if (!token.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt++, 5))) + Random.Shared.NextDouble()), token);
        }
    }
}
