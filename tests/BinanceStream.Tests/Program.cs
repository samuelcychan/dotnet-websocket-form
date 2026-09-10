using System.Globalization;
using BinanceStream;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception($"FAIL: {name}");
    Console.WriteLine($"PASS: {name}");
    checks++;
}
CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
var trade = (Trade)MarketEventParser.Parse("""{"e":"trade","s":"BTCUSDT","t":42,"p":"123.45678901","q":"0.001","T":1720000000000,"m":true}""");
Check(trade.Price == 123.45678901m && trade.Quantity == .001m && trade.Id == 42 && trade.BuyerIsMaker, "Decimal precision independent of culture");
var aggregate = (Trade)MarketEventParser.Parse("""{"stream":"ethusdt@aggTrade","data":{"e":"aggTrade","s":"ETHUSDT","a":99,"p":"1.25","q":"2","T":1720000000000,"m":false}}""");
Check(aggregate.Aggregated && aggregate.Id == 99 && aggregate.Symbol == "ETHUSDT", "Combined aggregate trade routing");
var book = (BookTicker)MarketEventParser.Parse("""{"u":123,"s":"BTCUSDT","b":"100.01","B":"2","a":"100.02","A":"3"}""");
Check(book.AskPrice - book.BidPrice == .01m && book.AskQuantity == 3, "Book ticker without event discriminator");
Check(MarketEventParser.Parse("""{"e":"future"}""") is UnknownEvent, "Unknown events retained");
Check(new Subscription("BTCUSDT", StreamKind.AggregateTrade).ToString() == "btcusdt@aggTrade", "Case-sensitive stream suffix");
try { _ = new Subscription("btc/usdt", StreamKind.Trade); throw new Exception("Invalid symbol accepted"); }
catch (ArgumentException) { Check(true, "Invalid symbol rejected"); }
var path = Path.GetTempFileName();
try
{
    await File.WriteAllTextAsync(path, "\n{\"e\":\"future\"}\n{\"e\":\"future\"}\n");
    var count = 0;
    await foreach (var item in FileReplay.ReadAsync(path, TimeSpan.Zero)) count++;
    Check(count == 2, "Replay skips blank lines and completes");
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    try
    {
        await foreach (var item in FileReplay.ReadAsync(path, TimeSpan.Zero, cts.Token)) { }
        throw new Exception("Cancellation ignored");
    }
    catch (OperationCanceledException) { Check(true, "Replay cancellation"); }
}
finally { File.Delete(path); }
Console.WriteLine($"{checks} checks passed.");

if (args.Contains("--live"))
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var client = new BinanceClient();
    client.StatusChanged += Console.WriteLine;
    var liveCount = 0;
    await foreach (var item in client.StreamAsync([
        new Subscription("BTCUSDT", StreamKind.Trade),
        new Subscription("BTCUSDT", StreamKind.BookTicker)], timeout.Token))
    {
        Console.WriteLine(item);
        if (++liveCount == 3) break;
    }
    Check(liveCount == 3, "Live WebSocket receives three events and disposes");
}
