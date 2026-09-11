using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MQTTnet;
using Telemetry.Core;
using Telemetry.Mqtt;
using Telemetry.Storage;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name); checks++;
}
void Reject(Action action, string name)
{
    try { action(); } catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException) { Check(true, name); return; }
    throw new Exception("FAIL: accepted " + name);
}
CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
var now = DateTimeOffset.UtcNow;
string Payload(double value, string id, DateTimeOffset? time = null) => JsonSerializer.Serialize(new
    { schemaVersion = 1, deviceId = "sensor-01", messageId = id, timestamp = time ?? DateTimeOffset.UtcNow, metric = "pressure", value, unit = "hPa" });
var baseline = ReadingParser.Parse(Payload(1013.2, "first", now), "test", "emulator/sensor-01/telemetry", receivedAt: now);
Check(baseline.Value == 1013.2 && baseline.Timestamp == now, "Invariant sensor JSON and UTC timestamp");
Reject(() => ReadingParser.Parse("{}", "test", "topic"), "Missing schema rejected");
Reject(() => ReadingParser.Parse("{}", "test", "topic", flatPressureDevice: "sensor-01"), "Missing pressure is not zero");
Reject(() => ReadingParser.Parse("{\"pressure\":\"10\"}", "test", "topic", flatPressureDevice: "sensor-01"), "String pressure rejected");
Reject(() => ReadingParser.Parse(Payload(1, "id").Replace("\"value\":1", "\"value\":1e999"), "test", "topic"), "Non-finite sensor value rejected");
Reject(() => ReadingParser.Parse(new string('x', 65537), "test", "topic"), "Oversized payload rejected");
Check(ReadingParser.Parse("{\"pressure\":1013.2}", "test", "topic", flatPressureDevice: "s").ReceiptTimeUsed, "Explicit flat pressure compatibility");
Reject(() => new MqttProfile(Topic: "a/#/b").Validate(), "Invalid topic filter rejected");
var rule = new AlertRule(960, 1040, 1, 30);
Check(rule.Next("Normal", 960) == "Normal" && rule.Next("Normal", 1040) == "Normal", "Threshold equality is normal");
Check(rule.Next("Low", 960.5) == "Low" && rule.Next("Low", 961) == "Normal", "Hysteresis recovery boundary");
Reject(() => new AlertRule(10, 1).Validate(), "Invalid threshold ordering");
var directory = Path.Combine(Path.GetTempPath(), "telemetry-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var store = new TelemetryStore(Path.Combine(directory, "readings.db"));
try
{
    store.Initialize(); store.Initialize();
    store.SaveRule(baseline.SeriesKey, rule);
    Check(store.Write(baseline).Inserted && !store.Write(baseline).Inserted, "Stable message IDs deduplicate");
    var second = baseline with { Value = 950, MessageId = "low", Timestamp = now.AddSeconds(1), ReceivedAt = now.AddSeconds(1) };
    Check(store.Write(second).Transition == "Normal → Low", "Low activation stored atomically");
    var restarted = new TelemetryStore(store.Path); restarted.Initialize();
    Check(restarted.Write(second with { MessageId = "low-2", Timestamp = now.AddSeconds(2), ReceivedAt = now.AddSeconds(2) }).Transition is null, "Restart preserves alert state");
    Check(store.Write(second with { MessageId = "retained", Value = 1050, Retained = true }).Transition is null, "Retained reading cannot trigger alerts");
    Check(store.Write(second with { MessageId = "late", Value = 1050, Timestamp = now.AddMinutes(-1) }).Transition is null, "Late reading cannot trigger alerts");
    Check(store.Write(second with { MessageId = "recovery", Value = 1013.2, Timestamp = now.AddSeconds(3), ReceivedAt = now.AddSeconds(3) }).Transition == "Low → Normal", "Alert recovery persisted");
    var events = store.Alerts();
    Check(events.Count == 2 && !events[0].Notify, "Cooldown suppresses notification without losing history");
    store.Acknowledge(events[0].Id);
    Check(store.Alerts()[0].Acknowledged, "Alert acknowledgement persisted");
    var filter = new HistoryFilter(now.AddMinutes(-2), now.AddMinutes(1));
    Check(store.Query(filter).Count == 6, "History includes valid retained and late readings");
    Check(store.Query(new(now, now.AddSeconds(1))).Count == 1, "Half-open time range excludes end boundary");
    var firstPage = store.Query(filter, limit: 2);
    var nextPage = store.Query(filter, firstPage[^1].Id, limit: 2);
    Check(firstPage.Count == 2 && nextPage[0].Id > firstPage[^1].Id, "History pagination has no overlapping IDs");
    store.Write(baseline with { MessageId = null, DeviceId = "=FORMULA,\"quoted\"" });
    var csvPath = Path.Combine(directory, "export.csv");
    var count = await store.ExportAsync(csvPath, filter, CancellationToken.None);
    var csv = await File.ReadAllTextAsync(csvPath);
    Check(count == 7 && csv.Contains("1013.2") && csv.Contains("\"'=FORMULA,\"\"quoted\"\"\""), "CSV full filter, invariant values, escaping and formula safety");
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        try { await store.ExportAsync(Path.Combine(directory, "cancelled.csv"), filter, cancelled.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { Check(!Directory.GetFiles(directory, "*.partial").Any(), "Cancelled export removes partial output"); }
    }
    // A local protocol fixture checks the actual wire request and fragmented response.
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    try
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            var stream = peer.GetStream(); var request = new byte[12];
            await stream.ReadExactlyAsync(request, deadline.Token);
            Check(request[6] == 1 && request[7] == 3 && request[8] == 0 && request[9] == 0 && request[11] == 1, "Modbus unit/function/zero-based address");
            var response = new byte[] { request[0], request[1], 0, 0, 0, 5, 1, 3, 2, 0, 0 };
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(9), 10132);
            await stream.WriteAsync(response.AsMemory(0, 8), deadline.Token);
            await stream.WriteAsync(response.AsMemory(8), deadline.Token);
        });
        var raw = await ModbusRegisterClient.ReadAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, 1, 0, TimeSpan.FromSeconds(5));
        await server;
        Check(raw == 10132 && Math.Abs(raw * .1 - 1013.2) < 1e-9, "Fragmented Modbus response and pressure scaling");
    }
    finally { listener.Stop(); }

    var brokerIndex = Array.IndexOf(args, "--broker");
    if (brokerIndex >= 0)
    {
        var host = args[brokerIndex + 1]; var port = int.Parse(args[brokerIndex + 2], CultureInfo.InvariantCulture);
        var topic = "tests/" + Guid.NewGuid().ToString("N");
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new MqttSource();
        source.StatusChanged += value => { Console.WriteLine("MQTT: " + value); if (value == "Connected") connected.TrySetResult(); };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var seen = 0;
        var subscriber = source.RunAsync(new(host, port, topic, "integration", "test-" + Guid.NewGuid().ToString("N")), null,
            (r, _) => { store.Write(r); Interlocked.Increment(ref seen); return Task.CompletedTask; }, timeout.Token);
        await connected.Task.WaitAsync(timeout.Token);
        using var publisher = new MqttClientFactory().CreateMqttClient();
        await publisher.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(host, port).Build(), timeout.Token);
        async Task Publish(string payload) => await publisher.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce).Build(), timeout.Token);
        var message = Payload(1013.2, "integration-one");
        await Publish(message); await Publish(message); await Publish("{}");
        while (Volatile.Read(ref seen) < 2 || source.Rejected < 1) await Task.Delay(50, timeout.Token);
        Check(store.Query(new(now.AddMinutes(-2), DateTimeOffset.UtcNow.AddMinutes(1), Source: "integration")).Count == 1, "HiveMQ QoS1 delivery and persisted deduplication");
        Check(source.Rejected == 1, "MQTT malformed payload counted and acknowledged");
        timeout.Cancel();
        try { await subscriber; } catch (OperationCanceledException) { }
        Check(subscriber.IsCompleted, "MQTT disconnect terminates subscriber");
        await publisher.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build());

        var faultSource = new MqttSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        faultSource.StatusChanged += s => { if (s == "Connected") ready.TrySetResult(); };
        using var faultTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var faultProfile = new MqttProfile(host, port, topic, "fault-test", "fault-" + Guid.NewGuid().ToString("N"));
        var faultTask = faultSource.RunAsync(faultProfile, null,
            (_, _) => throw new IOException("Simulated full disk"), faultTimeout.Token);
        await ready.Task.WaitAsync(faultTimeout.Token);
        await publisher.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(host, port).Build(), faultTimeout.Token);
        await publisher.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(Payload(1010, "fault"))
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce).Build(), faultTimeout.Token);
        try { await faultTask; throw new Exception("Storage failure was swallowed"); }
        catch (IOException ex) { Check(ex.InnerException?.Message == "Simulated full disk", "Storage fault stops MQTT ingestion"); }
        var redelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var recoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var recovery = new MqttSource().RunAsync(faultProfile, null,
            (r, _) => { if (r.MessageId == "fault") redelivered.TrySetResult(); return Task.CompletedTask; }, recoveryTimeout.Token);
        await redelivered.Task.WaitAsync(recoveryTimeout.Token);
        Check(true, "Unacknowledged storage failure redelivers after reconnect");
        recoveryTimeout.Cancel();
        try { await recovery; } catch (OperationCanceledException) { }
        await publisher.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build());
        var bridgeIndex = Array.IndexOf(args, "--bridge");
        if (bridgeIndex >= 0)
        {
            await BridgeCheck.RunAsync(host, port, args[bridgeIndex + 1], directory, store);
            Check(true, "Modbus fixture → bridge executable → HiveMQ → SQLite values match");
        }
        var restartIndex = Array.IndexOf(args, "--restart-broker");
        if (restartIndex >= 0)
        {
            await BrokerRestartCheck.RunAsync(host, port, args[restartIndex + 1]);
            Check(true, "HiveMQ retained data survives container restart");
        }
    }

    var backupPath = Path.Combine(directory, "backup.db");
    store.Backup(backupPath);
    Check(new TelemetryStore(backupPath).LatestId() == store.LatestId(), "Online SQLite backup includes committed readings");
    var cutoffId = store.LatestId();
    var removed = store.DeleteReadingsBefore(DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);
    Check(removed > 0 && store.Query(filter).Count == 0 && store.Alerts().Count == 2, "Manual retention removes readings but preserves alert history");
    store.Write(baseline with { MessageId = "after-retention" });
    Check(store.LatestId() > cutoffId, "Ingestion IDs remain monotonic after retention");
    store.DeleteRule(baseline.SeriesKey);
    Check(!store.Rules()[0].Enabled, "Disabling preserves versioned rule record");
    store.SaveRule(baseline.SeriesKey, rule);
    Check(store.Rules()[0].Enabled && store.Rules()[0].Version == 3, "Re-enabling advances rule version");
    Console.WriteLine($"{checks} checks passed. Artifacts: {directory}");
}
finally { SqliteConnection.ClearAllPools(); }
