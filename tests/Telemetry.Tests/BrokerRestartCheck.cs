using System.Diagnostics;
using MQTTnet;
using Telemetry.Mqtt;

internal static class BrokerRestartCheck
{
    public static async Task RunAsync(string host, int port, string composeFile)
    {
        var topic = "restart-test/" + Guid.NewGuid().ToString("N");
        var payload = System.Text.Json.JsonSerializer.Serialize(new { schemaVersion = 1, deviceId = "restart-test",
            messageId = "retained-one", timestamp = DateTimeOffset.UtcNow, metric = "pressure", value = 1013.2, unit = "hPa" });
        using var publisher = new MqttClientFactory().CreateMqttClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await publisher.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(host, port).Build(), deadline.Token);
        await publisher.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).WithRetainFlag()
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce).Build(), deadline.Token);
        await publisher.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), deadline.Token);
        var start = new ProcessStartInfo("docker") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "compose", "-f", Path.GetFullPath(composeFile), "restart", "hivemq" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Docker could not start.");
        await process.WaitForExitAsync(deadline.Token);
        if (process.ExitCode != 0) throw new IOException("HiveMQ restart failed.");
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new MqttSource();
        source.StatusChanged += s => Console.WriteLine("Restart probe: " + s);
        var task = source.RunAsync(new(host, port, topic, "restart-probe", Guid.NewGuid().ToString("N")), null,
            (r, _) => { if (r.Retained && r.MessageId == "retained-one" && r.Value == 1013.2) received.TrySetResult(); return Task.CompletedTask; }, deadline.Token);
        try
        {
            await received.Task.WaitAsync(deadline.Token);
            await publisher.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(host, port).Build(), deadline.Token);
            await publisher.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload("").WithRetainFlag().Build(), deadline.Token);
            await publisher.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), deadline.Token);
        }
        finally { deadline.Cancel(); try { await task; } catch (OperationCanceledException) { } }
    }
}
