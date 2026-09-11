using System.Text.Json;
using MQTTnet;
using Telemetry.Core;
using Telemetry.Mqtt;

if (args.Length != 1)
{
    Console.WriteLine("Usage: ModbusMqttBridge <configuration.json>. Ctrl+C stops. MQTT password: MQTT_PASSWORD environment variable.");
    return 1;
}
var options = JsonSerializer.Deserialize<BridgeOptions>(await File.ReadAllTextAsync(args[0]),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new ArgumentException("Missing configuration.");
options.Validate();
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var mqtt = new MqttClientFactory().CreateMqttClient();
var profile = new MqttProfile(options.MqttHost, options.MqttPort, options.Topic, "modbus-bridge", options.ClientId, options.Tls, options.Username);
var runId = Guid.NewGuid().ToString("N");
long sequence = 0;
string? pending = null;
try
{
    while (!stop.IsCancellationRequested)
    {
        try
        {
            if (!mqtt.IsConnected)
            {
                var connection = await mqtt.ConnectAsync(profile.Options(Environment.GetEnvironmentVariable("MQTT_PASSWORD")), stop.Token);
                if (connection.ResultCode != MqttClientConnectResultCode.Success) throw new IOException("MQTT connection refused.");
            }
            if (pending is null)
            {
                var raw = await ModbusRegisterClient.ReadAsync(options.ModbusHost, options.ModbusPort, options.UnitId,
                    options.Address, TimeSpan.FromMilliseconds(options.TimeoutMs), stop.Token);
                var value = raw * options.Scale + options.Offset;
                if (!double.IsFinite(value)) throw new FormatException("Scaled value is not finite.");
                pending = JsonSerializer.Serialize(new { schemaVersion = 1, deviceId = options.DeviceId,
                    messageId = $"{runId}-{++sequence}", timestamp = DateTimeOffset.UtcNow, metric = options.Metric,
                    value, unit = options.Unit });
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var result = await mqtt.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(options.Topic)
                .WithPayload(pending).WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce).Build(), deadline.Token);
            if ((int)result.ReasonCode >= 128) throw new IOException("MQTT publish rejected.");
            Console.WriteLine($"{DateTimeOffset.Now:O} Published reading {sequence} to {options.Topic}");
            pending = null;
            if (options.MaximumReadings > 0 && sequence >= options.MaximumReadings) break;
            await Task.Delay(options.PollIntervalMs, stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{DateTimeOffset.Now:O} {ex.GetType().Name}: {ex.Message}; retry in 2 seconds. Pending: {pending is not null}");
            await Task.Delay(2000, stop.Token);
        }
    }
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
finally
{
    if (mqtt.IsConnected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await mqtt.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), deadline.Token); }
        catch (Exception) { }
    }
    if (pending is not null) Console.Error.WriteLine("Stopped with one unconfirmed reading in memory; it is not durably spooled.");
}
return 0;

sealed record BridgeOptions
{
    public string ModbusHost { get; init; } = "127.0.0.1";
    public int ModbusPort { get; init; } = 502;
    public byte UnitId { get; init; } = 1;
    public ushort Address { get; init; }
    public double Scale { get; init; } = 0.1;
    public double Offset { get; init; }
    public int PollIntervalMs { get; init; } = 1000;
    public int TimeoutMs { get; init; } = 2000;
    public int MaximumReadings { get; init; }
    public string DeviceId { get; init; } = "sensor-01";
    public string Metric { get; init; } = "pressure";
    public string Unit { get; init; } = "hPa";
    public string MqttHost { get; init; } = "127.0.0.1";
    public int MqttPort { get; init; } = 1883;
    public string Topic { get; init; } = "emulator/sensor-01/telemetry";
    public string ClientId { get; init; } = "modbus-pressure-bridge";
    public bool Tls { get; init; }
    public string? Username { get; init; }
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ModbusHost) || ModbusPort is < 1 or > 65535 ||
            PollIntervalMs < 100 || TimeoutMs is < 100 or > 30000 || MaximumReadings < 0 || !double.IsFinite(Scale) || !double.IsFinite(Offset) ||
            Topic.Contains('+') || Topic.Contains('#') || string.IsNullOrWhiteSpace(DeviceId) ||
            string.IsNullOrWhiteSpace(Metric) || string.IsNullOrWhiteSpace(Unit)) throw new ArgumentException("Invalid bridge configuration.");
        new MqttProfile(MqttHost, MqttPort, Topic, ClientId: ClientId).Validate();
    }
}
