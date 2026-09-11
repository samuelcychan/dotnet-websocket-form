# Binance Stream + Windows Forms Monitor

A C#/.NET 10 implementation inspired by [Marfusios/binance-client-websocket](https://github.com/Marfusios/binance-client-websocket). This is an independent implementation, not a fork or a drop-in replacement. It uses the built-in `ClientWebSocket`, `System.Text.Json`, and async streams; no third-party runtime packages are required.

## Run

Requires Windows and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet build BinanceStream.slnx -c Release
dotnet run --project src/BinanceMonitor -c Release
```

For this workspace, an SDK was installed locally without modifying PATH. You can run:

```powershell
& "$env:LOCALAPPDATA\dotnet-websocket-sdk\dotnet.exe" run --project src/BinanceMonitor -c Release
```

Enter comma-separated symbols, select Trade, AggregateTrade, or BookTicker, then Connect. Disconnect before changing subscriptions. Replay sample shows three recorded events without accessing the network. The grid keeps the latest 500 rows; a bounded 1,000-event queue drops the oldest pending display events under heavy load and reports display skips. This demo is not a complete event recorder.

## Live price chart

The monitor charts the selected symbol alongside the event grid. Trade and aggregate-trade prices appear in gold; best bid and ask appear in teal and blue. Use **Chart symbol** to switch between symbols received in the current session. Each symbol retains up to 300 displayed samples (at most 100 symbols), with automatic price scaling. A new connection or replay clears the chart.

Samples are evenly spaced by arrival order, not elapsed time. The labels show local display time because book-ticker messages do not contain exchange timestamps. Replay uses the same chart path; the bundled three-event sample produces sparse points. Chart history shares the bounded display pipeline and may skip events under load; it is not durable storage. The chart uses native WinForms drawing with no additional packages.

## Library usage

```csharp
using BinanceStream;

var client = new BinanceClient();
client.StatusChanged += Console.WriteLine;
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
try
{
    await foreach (var message in client.StreamAsync([
        new Subscription("BTCUSDT", StreamKind.Trade),
        new Subscription("ETHUSDT", StreamKind.BookTicker)
    ], cancellation.Token))
    {
        if (message is Trade trade)
            Console.WriteLine($"{trade.Symbol}: {trade.Price} × {trade.Quantity}");
    }
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
```

The library targets `net10.0` and can also be used from non-Windows applications. Each enumeration owns a connection; cancelling or breaking enumeration disposes its socket. Consumers process events sequentially and should keep handlers short or supply their own bounded queue. Status callbacks run on the transport thread; marshal to the UI before modifying controls.

Connections use the public market-data endpoint `wss://data-stream.binance.vision`. Combined stream URLs restore the same subscriptions on every reconnect. Failed connections use exponential backoff with jitter, capped around one minute; successful connections lasting 30 seconds reset the backoff. Connection attempts have a 20-second timeout; incomplete/no data messages have a two-minute timeout. Text messages are assembled across frames and limited to 1 MiB. Server pings are handled by .NET. Network access to Binance may depend on your location and network.

## Replay and checks

Replay accepts one raw or combined Binance JSON object per nonblank line, with a fixed delay between records. It preserves file order, not historical timing. Invalid replay records stop enumeration with a parsing exception.

```csharp
await foreach (var message in FileReplay.ReadAsync("samples/market.jsonl", TimeSpan.FromMilliseconds(500)))
    Console.WriteLine(message);
```

```powershell
# Dependency-free regression checks; failures return a nonzero exit code.
dotnet run --project tests/BinanceStream.Tests -c Release
# Optional network smoke test (30-second deadline).
dotnet run --project tests/BinanceStream.Tests -c Release -- --live
```

## Scope

Implemented: typed trades, aggregate trades, best bid/ask quotes, decimal precision, raw/combined JSON parsing, cancellation, reconnection, diagnostics, file replay, and a Forms demo. Unknown event payloads remain available as `UnknownEvent`. The client accepts up to 100 unique streams per connection as an application limit.

Authenticated user streams, order placement, candlesticks, order-book depth reconstruction, Rx observables, and subscription changes on an existing socket are not implemented. Best bid/ask is not a reconstructed order book. Prices and sample records are market data, not trading signals.

Protocol reference: [Binance Spot WebSocket streams](https://github.com/binance/binance-spot-api-docs/blob/master/web-socket-streams.md).
