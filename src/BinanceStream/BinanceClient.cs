using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;

namespace BinanceStream;

/// <summary>Each enumeration owns its socket. Cancel enumeration to disconnect.</summary>
public sealed class BinanceClient
{
    public Uri Endpoint { get; }
    public event Action<string>? StatusChanged;

    public BinanceClient(Uri? endpoint = null)
    {
        Endpoint = endpoint ?? new Uri("wss://data-stream.binance.vision");
        if (Endpoint.Scheme != "wss" || Endpoint.AbsolutePath != "/" ||
            Endpoint.Query.Length > 0 || Endpoint.Fragment.Length > 0 || Endpoint.UserInfo.Length > 0)
            throw new ArgumentException("Endpoint must be a secure WebSocket origin without a path or query.", nameof(endpoint));
    }

    public async IAsyncEnumerable<MarketEvent> StreamAsync(IEnumerable<Subscription> subscriptions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var streams = subscriptions.Distinct().Select(x => x.ToString()).ToArray();
        if (streams.Length is < 1 or > 100)
            throw new ArgumentException("Select between 1 and 100 streams.", nameof(subscriptions));
        var uri = new Uri(Endpoint, "/stream?streams=" + string.Join('/', streams));
        var attempts = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            // ClientWebSocket automatically responds to server ping control frames.
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            Exception? failure = null;
            try
            {
                Report("Connecting");
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                await socket.ConnectAsync(uri, connectTimeout.Token).ConfigureAwait(false);
                Report("Connected");
            }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or OperationCanceledException)
            { failure = ex; }

            var connectedAt = DateTimeOffset.UtcNow;
            while (failure is null && !cancellationToken.IsCancellationRequested)
            {
                MarketEvent? item = null;
                try
                {
                    var json = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
                    item = MarketEventParser.Parse(json);
                    if (item is UnknownEvent unknown && unknown.Json.Contains("serverShutdown", StringComparison.Ordinal))
                        throw new WebSocketException("Server is shutting down.");
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
                { failure = ex; }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException or
                    InvalidOperationException or KeyNotFoundException or ArgumentOutOfRangeException or OverflowException)
                { Report($"Ignored malformed message: {ex.Message}"); }
                if (item is not null) yield return item;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow - connectedAt > TimeSpan.FromSeconds(30)) attempts = 0;
            var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(attempts++, 6))) + Random.Shared.NextDouble());
            Report($"Reconnecting in {delay.TotalSeconds:F1}s: {failure?.Message}");
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Report(string status)
    {
        // Observers cannot interrupt the transport loop.
        foreach (Action<string> observer in StatusChanged?.GetInvocationList() ?? [])
            try { observer(status); } catch { /* Isolate consumer failures. */ }
    }

    private static async Task<string> ReceiveAsync(ClientWebSocket socket, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var message = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException($"Server closed: {result.CloseStatus} {result.CloseStatusDescription}");
            if (result.MessageType != WebSocketMessageType.Text)
                throw new IOException("Expected a text message.");
            if (message.Length + result.Count > 1024 * 1024)
                throw new IOException("Message exceeded 1 MiB limit.");
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
    }
}
