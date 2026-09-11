using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Telemetry.Mqtt;
using Telemetry.Storage;

internal static class BridgeCheck
{
    public static async Task RunAsync(string broker, int brokerPort, string bridgeDll, string directory, TelemetryStore store)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var topic = "bridge-test/" + Guid.NewGuid().ToString("N");
        var profile = new MqttProfile(broker, brokerPort, topic, "bridge-probe", Guid.NewGuid().ToString("N"));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new MqttSource();
        var count = 0;
        source.StatusChanged += s => { if (s == "Connected") ready.TrySetResult(); };
        var subscriber = source.RunAsync(profile, null, (r, _) =>
        {
            store.Write(r); if (Interlocked.Increment(ref count) == 3) received.TrySetResult(); return Task.CompletedTask;
        }, deadline.Token);
        Process? bridge = null;
        Task? server = null;
        try
        {
            await ready.Task.WaitAsync(deadline.Token);
            server = Task.Run(async () =>
            {
                foreach (var raw in new ushort[] { 10132, 9500, 10500 })
                {
                    using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
                    var stream = peer.GetStream(); var request = new byte[12];
                    await stream.ReadExactlyAsync(request, deadline.Token);
                    if (request[6] != 1 || request[7] != 3 || request[8] != 0 || request[9] != 0)
                        throw new IOException("Unexpected bridge Modbus request.");
                    var response = new byte[] { request[0], request[1], 0, 0, 0, 5, 1, 3, 2, 0, 0 };
                    BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(9), raw);
                    await stream.WriteAsync(response, deadline.Token);
                }
            }, deadline.Token);
            var configuration = Path.Combine(directory, "bridge.json");
            await File.WriteAllTextAsync(configuration, JsonSerializer.Serialize(new
            {
                modbusHost = "127.0.0.1", modbusPort = ((IPEndPoint)listener.LocalEndpoint).Port,
                mqttHost = broker, mqttPort = brokerPort, topic, pollIntervalMs = 100, maximumReadings = 3,
                clientId = "bridge-" + Guid.NewGuid().ToString("N")
            }), deadline.Token);
            var runtimeRoot = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "../../.."));
            var dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Path.Combine(runtimeRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            var start = new ProcessStartInfo(dotnetHost)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.GetFullPath(bridgeDll)); start.ArgumentList.Add(configuration);
            bridge = Process.Start(start) ?? throw new IOException("Could not start bridge.");
            var output = bridge.StandardOutput.ReadToEndAsync(deadline.Token);
            var errors = bridge.StandardError.ReadToEndAsync(deadline.Token);
            await bridge.WaitForExitAsync(deadline.Token);
            if (bridge.ExitCode != 0) throw new Exception(await errors);
            Console.Write(await output);
            await received.Task.WaitAsync(deadline.Token); await server;
            var rows = store.Query(new(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1), Source: "bridge-probe"));
            var expected = new[] { 1013.2, 950, 1050 };
            if (rows.Count != 3 || rows.Where((r, i) => Math.Abs(r.Reading.Value - expected[i]) > 1e-8).Any())
                throw new Exception("Bridge values did not match source registers.");
        }
        finally
        {
            deadline.Cancel(); listener.Stop();
            if (bridge is not null) { if (!bridge.HasExited) bridge.Kill(true); bridge.Dispose(); }
            try { await subscriber; } catch (OperationCanceledException) { }
            if (server is not null) { try { await server; } catch (OperationCanceledException) { } }
        }
    }
}
